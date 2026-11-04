using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// Lifecycle tests (SPEC §12.3): relay + fake child process.
static class LifecycleTests
{
    static readonly string[] StreamJson = { "--output-format", "stream-json" };

    // ---- relaying, EOF, exit codes ----

    public static void EchoTranslatesBothWays()
    {
        using (var r = RelayRun.Start("echo", StreamJson))
        {
            r.In.WriteText("{\"cwd\":\"C:\\\\app\\\\x\",\"text\":\"C:\\\\app\"}\n");
            r.In.CloseWriter();  // stdin EOF → child stdin closed → echo child exits
            T.Equal(0, r.Wait());
            T.Equal("{\"cwd\":\"C:\\\\app\\\\x\",\"text\":\"C:\\\\app\"}\n", r.Out.Text, "stdout = only protocol, translated back");
            T.Equal("{\"cwd\":\"/work/app/x\",\"text\":\"C:\\\\app\"}\n", r.Err.Text, "child received Linux path");
        }
    }

    public static void ChildIgnoringEofKeepsRelaying()
    {
        using (var r = RelayRun.Start("ignore-eof", StreamJson))
        {
            r.In.CloseWriter();
            T.Equal(0, r.Wait());
            T.Equal("{\"cwd\":\"C:\\\\app\",\"after\":\"eof\"}\n", r.Out.Text);
        }
    }

    public static void ExitWithStdinOpen()
    {
        using (var r = RelayRun.Start("exit:3", StreamJson))
        {
            T.Equal(3, r.Wait(), "relay exits although stdin stays open");
            r.In.WriteText("{\"late\":1}\n");  // write after exit must not crash anything
        }
    }

    public static void ExitCodes()
    {
        foreach (int code in new[] { 0, 3, -1 })
            using (var r = RelayRun.Start("exit:" + code, StreamJson))
            {
                int expected = code == -1 && !T.IsWindows ? 255 : code;  // Linux exit status is 8-bit
                T.Equal(expected, r.Wait(), "exit code " + code);
            }
    }

    public static void ChildCrash()
    {
        using (var r = RelayRun.Start("crash", StreamJson))
        {
            T.True(r.Wait() != 0, "non-zero exit");
            T.Contains("fake crash", r.Err.Text, "crash text on stderr");
            T.Equal("", r.Out.Text);
        }
    }

    public static void StartFailure()
    {
        using (var r = RelayRun.Start("echo", StreamJson, null, "/nonexistent/agent.exe"))
        {
            T.Equal(1, r.Wait());
            T.Contains("agent-relay-claude: cannot start /nonexistent/agent.exe: ", r.Err.Text);
            T.Equal("", r.Out.Text);
        }
    }

    public static void StderrBytesIdentical()
    {
        using (var r = RelayRun.Start("stderr-bytes", StreamJson))
        {
            T.Equal(0, r.Wait());
            T.Equal(Convert.ToBase64String(FakeChild.StderrBytes), Convert.ToBase64String(r.Err.Bytes()));
        }
    }

    public static void BigLineIntact()
    {
        using (var r = RelayRun.Start("big-line", StreamJson))
        {
            T.Equal(0, r.Wait());
            string expected = "{\"text\":\"" + new string('a', 10 * 1024 * 1024) + "\",\"cwd\":\"C:\\\\app\"}\n";
            T.True(r.Out.Text == expected, "10 MB line intact and translated (length " + r.Out.Text.Length + ")");
        }
    }

    public static void UnterminatedLastLineIntact()
    {
        using (var r = RelayRun.Start("unterminated", StreamJson))
        {
            T.Equal(0, r.Wait());
            T.Equal("{\"cwd\":\"C:\\\\app\"}\n{\"cwd\":\"/work/app\"}", r.Out.Text, "last fragment as-is, no newline added");
        }
    }

    public static void PassthroughPromptAtOnce()
    {
        using (var r = RelayRun.Start("prompt", new[] { "auth", "login" }))
        {
            T.True(r.Out.WaitFor("Password: ", 5000), "prompt without newline arrives before the answer");
            r.In.WriteText("x\n");
            T.Equal(0, r.Wait());
            T.Equal("Password: ok\n", r.Out.Text);
        }
    }

    public static void RelayStdoutClosed()
    {
        using (var r = RelayRun.Start("big-line", StreamJson, "{'level':'basic'}"))
        {
            r.Out.Broken = true;
            T.Equal(0, r.Wait(), "relay keeps draining child stdout and exits with child code");
            T.Contains("AGENT -> EDITOR write failed", r.LogText());
        }
    }

    public static void CleanupFailure()
    {
        string pidFile = Path.Combine(T.TempDir(), "grandchild.pid");
        var sw = Stopwatch.StartNew();
        using (var r = RelayRun.Start("grandchild", StreamJson, null, null, pidFile, true))
        {
            T.Equal(0, r.Wait(9000));
            sw.Stop();
            T.Contains("agent-relay-claude: cleanup failed: fake cleanup failure", r.Err.Text);
            T.True(sw.ElapsedMilliseconds < 7000, "relay exits within 5 s + margin (" + sw.ElapsedMilliseconds + " ms)");
            T.Equal("{\"cwd\":\"C:\\\\app\"}\n", r.Out.Text);
        }
        KillPidFile(pidFile);
    }

    // ---- Windows only: job object, out of process ----

    public static void GrandchildHoldingStdout()
    {
        T.WindowsOnly();
        string pidFile = Path.Combine(T.TempDir(), "grandchild.pid");
        Process relay = StartRelayUnderTest("grandchild", pidFile);
        string output = relay.StandardOutput.ReadToEnd();
        T.True(relay.WaitForExit(5000), "relay exits after child exit");
        T.Equal(0, relay.ExitCode);
        T.Equal("{\"cwd\":\"C:\\\\app\"}\n", output);
        T.True(!IsRunning(int.Parse(File.ReadAllText(pidFile))), "grandchild is gone");
    }

    public static void RelayKillKillsChild()
    {
        T.WindowsOnly();
        string pidFile = Path.Combine(T.TempDir(), "child.pid");
        Process relay = StartRelayUnderTest("pid-sleep", pidFile);
        for (int i = 0; i < 100 && !File.Exists(pidFile); i++) Thread.Sleep(50);
        Thread.Sleep(100);
        int child = int.Parse(File.ReadAllText(pidFile));
        T.True(IsRunning(child), "child running");
        relay.Kill();
        relay.WaitForExit();
        for (int i = 0; i < 40 && IsRunning(child); i++) Thread.Sleep(50);
        T.True(!IsRunning(child), "child killed with the relay");
    }

    static Process StartRelayUnderTest(string script, string arg)
    {
        var psi = new ProcessStartInfo(T.TestsExe, "--relay-under-test " + ArgQuoter.Quote(script) + " " + ArgQuoter.Quote(arg));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        return Process.Start(psi);
    }

    // Entry for "--relay-under-test <script> <arg>": real launcher with job object, real console streams.
    public static int RelayUnderTest(string[] args)
    {
        RelayHost host = RelayHost.Real();
        host.ExeDir = T.TempDir();
        host.ConfigPath = RelayRun.WriteConfig(host.ExeDir, null);
        host.LaunchDir = "C:\\app";
        host.Launcher = new FakeLauncher(new ProcessLauncher(true), args[1], args[2], null, false);
        return RelayMain.Run("claude", new ClaudeRules(), StreamJson, host);
    }

    static bool IsRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    static void KillPidFile(string pidFile)
    {
        try { Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))).Kill(); } catch (Exception) { }
    }

    // ---- logging ----

    static RelayRun RunEcho(string log, string input = "{\"cwd\":\"C:\\\\app\"}\n{\"id\":1}\n")
    {
        var r = RelayRun.Start("echo", StreamJson, log);
        r.In.WriteText(input);
        r.In.CloseWriter();
        T.Equal(0, r.Wait());
        return r;
    }

    public static void LogBasic()
    {
        using (RelayRun r = RunEcho("{'level':'basic'}"))
        {
            string log = r.LogText();
            foreach (string e in new[] { " LAUNCH]", " EOF]", " EXIT]", " AGENT STDERR]", "command: ", "-d Ubuntu --cd /work/app" })
                T.Contains(e, log);
            foreach (string e in new[] { "RECEIVED]", "FORWARDED]", "CHANGE]" }) T.NotContains(e, log);
        }
    }

    public static void LogChanges()
    {
        using (RelayRun r = RunEcho("{'level':'changes'}"))
        {
            string log = r.LogText();
            T.Contains("EDITOR -> AGENT CHANGE]\nfield=cwd: \"C:\\\\app\" -> \"/work/app\"", log);
            T.Contains("AGENT -> EDITOR CHANGE]\nfield=cwd: \"/work/app\" -> \"C:\\\\app\"", log);
            foreach (string e in new[] { "RECEIVED]", "FORWARDED]" }) T.NotContains(e, log);
        }
    }

    public static void LogFull()
    {
        using (RelayRun r = RunEcho("{'level':'full'}"))
        {
            string log = r.LogText();
            T.Contains("EDITOR -> AGENT RECEIVED]\n{\"cwd\":\"C:\\\\app\"}", log);
            T.Contains("EDITOR -> AGENT FORWARDED]\n{\"cwd\":\"/work/app\"}", log);
            T.Contains("EDITOR -> AGENT FORWARDED]\n(unchanged)", log);
            T.Contains("CHANGE]", log);
        }
    }

    public static void LogOffNoFile()
    {
        using (RelayRun r = RunEcho(null))
            T.True(!Directory.Exists(r.LogDir), "no log dir/file for level off");
    }

    public static void LogFilePerSessionAndPruning()
    {
        string dir = T.TempDir();
        for (int i = 1; i <= 5; i++) File.WriteAllText(Path.Combine(dir, "agent-relay-claude-20000101-00000" + i + "-0000000" + i + ".log"), "old");
        File.WriteAllText(Path.Combine(dir, "agent-relay-codex-20000101-000000-00000000.log"), "other agent");
        string log = "{'level':'basic','keep':3,'dir':" + JsonPathWalker.Encode(dir).Replace('"', '\'') + "}";
        RunEcho(log).Dispose();
        RunEcho(log).Dispose();
        string[] files = Directory.GetFiles(dir, "agent-relay-claude-*.log");
        Array.Sort(files, StringComparer.Ordinal);
        T.Equal(3, files.Length, "keep=3");
        T.True(files[0].EndsWith("00000005.log"), "newest old file kept: " + files[0]);
        T.True(!files[1].Contains("20000101") && !files[2].Contains("20000101"), "two new session files");
        T.Equal(1, Directory.GetFiles(dir, "agent-relay-codex-*.log").Length, "other agent's files untouched");
    }

    public static void LogPruningSkipsFileInUse()
    {
        T.WindowsOnly();
        string dir = T.TempDir(), busy = Path.Combine(dir, "agent-relay-claude-20000101-000000-00000000.log");
        File.WriteAllText(busy, "busy");
        string log = "{'level':'basic','keep':1,'dir':" + JsonPathWalker.Encode(dir).Replace('"', '\'') + "}";
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
            RunEcho(log).Dispose();
        T.True(File.Exists(busy), "file in use skipped");
        T.Equal(2, Directory.GetFiles(dir, "agent-relay-claude-*.log").Length);
    }

    public static void LogSizeLimit()
    {
        var input = new StringBuilder();
        for (int i = 0; i < 200; i++) input.Append("{\"cwd\":\"C:\\\\app\",\"n\":" + i + "}\n");
        using (RelayRun r = RunEcho("{'level':'full','maxSizeMB':0.05}", input.ToString()))
        {
            string log = r.LogText();
            T.Contains("LOG LIMIT]", log);
            T.True(log.IndexOf("LOG LIMIT]") > log.Length - 200, "LOG LIMIT is the last entry");
            long size = new FileInfo(r.LogFile()).Length;
            T.True(size <= 0.05 * 1024 * 1024 + 200, "size " + size + " <= cap + LOG LIMIT entry");
            T.Equal(input.ToString(), r.Out.Text, "relay continues");
        }
    }

    public static void LogDirIsFile()
    {
        string file = Path.Combine(T.TempDir(), "not-a-dir");
        File.WriteAllText(file, "");
        string log = "{'level':'full','dir':" + JsonPathWalker.Encode(file).Replace('"', '\'') + "}";
        using (RelayRun r = RunEcho(log))
        {
            string err = r.Err.Text;
            int first = err.IndexOf("agent-relay-claude: log disabled: ");
            T.True(first >= 0, "warning on stderr: " + err);
            T.True(err.IndexOf("log disabled", first + 40) < 0, "only one warning");
            T.Equal("{\"cwd\":\"C:\\\\app\"}\n{\"id\":1}\n", r.Out.Text, "relay continues");
        }
    }
}

// In-process relay run with pipes and a fake child (SPEC §12.3 harness).
class RelayRun : IDisposable
{
    public readonly TestPipe In = new TestPipe(), Out = new TestPipe(), Err = new TestPipe();
    public string Dir, LogDir;
    Thread thread;
    int code;

    public static string WriteConfig(string dir, string log)
    {
        string json = @"{'defaults':{'distribution':'Ubuntu'" + (log != null ? ",'log':" + log : "") +
                      @",'agents':{'claude':{'executable':'/usr/bin/claude'}}},
                       'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app'}]}";
        string path = Path.Combine(dir, "agent-relay.json");
        File.WriteAllText(path, T.J(json));
        return path;
    }

    // log: JSON (with ') for the "log" section, or null. fileName: override child exe (start failure).
    public static RelayRun Start(string script, string[] agentArgs, string log = null, string fileName = null,
                                 string scriptArg = null, bool failCleanup = false)
    {
        var r = new RelayRun();
        r.Dir = T.TempDir();
        r.LogDir = Path.Combine(r.Dir, "logs");
        var host = new RelayHost();
        host.ExeDir = r.Dir;
        host.ExePath = Path.Combine(r.Dir, "agent-relay-claude.exe");
        host.ConfigPath = WriteConfig(r.Dir, log);
        host.LaunchDir = "C:\\app";
        host.PathEnv = "";
        host.SystemRoot = "C:\\Windows";
        host.StdIn = r.In;
        host.StdOut = r.Out;
        host.StdErr = r.Err;
        host.Launcher = new FakeLauncher(new ProcessLauncher(false), script, scriptArg, fileName, failCleanup);
        r.thread = new Thread(() => r.code = RelayMain.Run("claude", new ClaudeRules(), agentArgs, host));
        r.thread.IsBackground = true;
        r.thread.Start();
        return r;
    }

    public int Wait(int ms = 8000)
    {
        if (!thread.Join(ms)) throw new AssertException("relay did not exit within " + ms + " ms");
        return code;
    }

    public string LogFile()
    {
        string[] files = Directory.GetFiles(LogDir, "agent-relay-claude-*.log");
        T.Equal(1, files.Length, "one log file");
        return files[0];
    }

    public string LogText() { return File.ReadAllText(LogFile(), Encoding.UTF8); }

    public void Dispose() { In.CloseWriter(); }
}

// Replaces the wsl.exe launch with this test exe in --fake-child mode.
class FakeLauncher : IChildLauncher
{
    readonly IChildLauncher inner;
    readonly string script, arg, fileName;
    readonly bool failCleanup;

    public FakeLauncher(IChildLauncher inner, string script, string arg, string fileName, bool failCleanup)
    {
        this.inner = inner;
        this.script = script;
        this.arg = arg;
        this.fileName = fileName;
        this.failCleanup = failCleanup;
    }

    public IChildProcess Start(LaunchSpec spec)
    {
        var s = new LaunchSpec();
        s.FileName = fileName ?? FakeChild.SelfFileName;
        s.Arguments = FakeChild.SelfArguments("--fake-child", script, arg);
        s.Environment = spec.Environment;
        IChildProcess child = inner.Start(s);
        return failCleanup ? new FailingCleanup(child) : child;
    }

    class FailingCleanup : IChildProcess
    {
        readonly IChildProcess c;
        public FailingCleanup(IChildProcess c) { this.c = c; }
        public Stream StdIn { get { return c.StdIn; } }
        public Stream StdOut { get { return c.StdOut; } }
        public Stream StdErr { get { return c.StdErr; } }
        public void WaitForExit() { c.WaitForExit(); }
        public int ExitCode { get { return c.ExitCode; } }
        public void Cleanup() { throw new Exception("fake cleanup failure"); }
    }
}

// In-memory blocking pipe: one writer, one reader; Read blocks until data or CloseWriter.
class TestPipe : Stream
{
    readonly object sync = new object();
    readonly MemoryStream data = new MemoryStream();
    long readPos;
    bool closed;
    public volatile bool Broken;  // Write throws, like a closed pipe

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Broken) throw new IOException("pipe is broken");
        lock (sync)
        {
            data.Position = data.Length;
            data.Write(buffer, offset, count);
            Monitor.PulseAll(sync);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (sync)
        {
            while (readPos == data.Length && !closed) Monitor.Wait(sync);
            if (readPos == data.Length) return 0;
            data.Position = readPos;
            int n = data.Read(buffer, offset, (int)Math.Min(count, data.Length - readPos));
            readPos += n;
            return n;
        }
    }

    public void WriteText(string s) { byte[] b = T.Bytes(s); Write(b, 0, b.Length); }

    public void CloseWriter() { lock (sync) { closed = true; Monitor.PulseAll(sync); } }

    public byte[] Bytes() { lock (sync) return data.ToArray(); }

    public string Text { get { return T.Utf8(Bytes()); } }

    public bool WaitFor(string text, int ms)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        lock (sync)
        {
            while (!T.Utf8(data.ToArray()).Contains(text))
            {
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return false;
                Monitor.Wait(sync, left);
            }
            return true;
        }
    }

    public override bool CanRead { get { return true; } }
    public override bool CanWrite { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override void Flush() { }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
    public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
    public override void SetLength(long v) { throw new NotSupportedException(); }
}
