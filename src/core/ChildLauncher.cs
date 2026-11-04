using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

public class LaunchException : Exception
{
    public LaunchException(string message) : base(message) { }
}

// Seam for tests: real process + job object, or fakes.
public interface IChildLauncher
{
    IChildProcess Start(LaunchSpec spec);  // throws LaunchException
}

public interface IChildProcess
{
    Stream StdIn { get; }
    Stream StdOut { get; }
    Stream StdErr { get; }
    void WaitForExit();
    int ExitCode { get; }
    void Cleanup();  // kill remaining descendants; throws on failure
}

public class ProcessLauncher : IChildLauncher
{
    readonly bool useJob;

    // useJob: assign the relay itself to a kill-on-close job before start (Windows only, SPEC §9).
    public ProcessLauncher(bool useJob) { this.useJob = useJob; }

    public IChildProcess Start(LaunchSpec spec)
    {
        IntPtr job = IntPtr.Zero;
        if (useJob && Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            try { job = JobObject.CreateAndJoin(); }
            catch (Exception e) { throw new LaunchException("cannot create job object: " + e.Message); }
        }

        var psi = new ProcessStartInfo(spec.FileName, spec.Arguments);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (spec.WorkingDirectory != null && Directory.Exists(spec.WorkingDirectory))
            psi.WorkingDirectory = spec.WorkingDirectory;
        foreach (var kv in spec.Environment) psi.EnvironmentVariables[kv.Key] = kv.Value;

        try
        {
            Process p = Process.Start(psi);
            return new RealChild(p, job);
        }
        catch (Exception e) { throw new LaunchException("cannot start " + spec.FileName + ": " + e.Message); }
    }

    class RealChild : IChildProcess
    {
        readonly Process p;
        readonly IntPtr job;

        public RealChild(Process p, IntPtr job) { this.p = p; this.job = job; }

        public Stream StdIn { get { return p.StandardInput.BaseStream; } }
        public Stream StdOut { get { return p.StandardOutput.BaseStream; } }
        public Stream StdErr { get { return p.StandardError.BaseStream; } }
        public void WaitForExit() { p.WaitForExit(); }
        public int ExitCode { get { return p.ExitCode; } }

        public void Cleanup()
        {
            if (job != IntPtr.Zero) JobObject.KillOthers(job);
        }
    }
}

static class JobObject
{
    const int JobObjectBasicProcessIdList = 3;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const uint PROCESS_TERMINATE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    // Job handle is never closed: the OS closes it when the relay exits, killing all members.
    public static IntPtr CreateAndJoin()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception();
        var info = new ExtendedLimit();
        info.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
            throw new Win32Exception();
        if (!AssignProcessToJobObject(job, GetCurrentProcess())) throw new Win32Exception();
        return job;
    }

    // Terminate every job process except the relay; at most 5 rounds.
    public static void KillOthers(IntPtr job)
    {
        uint self = (uint)Process.GetCurrentProcess().Id;
        const int max = 1024;
        int size = 8 + max * IntPtr.Size;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            for (int round = 0; round < 5; round++)
            {
                if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buf, size, IntPtr.Zero))
                    throw new Win32Exception();
                int count = Marshal.ReadInt32(buf, 4);
                bool others = false;
                for (int i = 0; i < count; i++)
                {
                    uint pid = (uint)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64();
                    if (pid == self) continue;
                    others = true;
                    IntPtr h = OpenProcess(PROCESS_TERMINATE, false, pid);
                    if (h == IntPtr.Zero) continue;  // already gone
                    TerminateProcess(h, 1);
                    CloseHandle(h);
                }
                if (!others) return;
                System.Threading.Thread.Sleep(50);
            }
            throw new Exception("job processes still running after 5 rounds");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
