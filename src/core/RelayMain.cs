using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

// Process environment, injectable for tests.
public class RelayHost
{
    public string ConfigPath, ExeDir, ExePath, LaunchDir, PathEnv, SystemRoot;
    public Stream StdIn, StdOut, StdErr;
    public IChildLauncher Launcher;

    public static RelayHost Real()
    {
        var h = new RelayHost();
        h.ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        h.ExePath = Assembly.GetEntryAssembly().Location;
        h.ConfigPath = Path.Combine(h.ExeDir, "agent-relay.json");
        h.LaunchDir = Environment.CurrentDirectory;
        h.PathEnv = Environment.GetEnvironmentVariable("PATH");
        h.SystemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows";
        h.StdIn = Console.OpenStandardInput();
        h.StdOut = Console.OpenStandardOutput();
        h.StdErr = Console.OpenStandardError();
        h.Launcher = new ProcessLauncher(true);
        return h;
    }
}

// args → config → resolve → launch → pump → exit (SPEC §2).
public static class RelayMain
{
    const string ToAgent = "EDITOR -> AGENT", ToEditor = "AGENT -> EDITOR";

    public static int Run(string agent, AgentRules rules, string[] args)
    {
        return Run(agent, rules, args, RelayHost.Real());
    }

    public static int Run(string agent, AgentRules rules, string[] args, RelayHost host)
    {
        var stderr = new StreamWriter(host.StdErr, new UTF8Encoding(false));
        stderr.AutoFlush = true;
        string prefix = "agent-relay-" + agent + ": ";
        RelayLog log = RelayLog.None(agent, stderr);
        try
        {
            string flag = RelayArgs.Flag(args);
            if (flag == RelayArgs.Version) return Print(host, "agent-relay-" + agent + " " + RelayVersion.Value);
            if (flag == RelayArgs.Help) return Print(host, RelayArgs.Usage(agent));
            if (flag != null && flag != RelayArgs.SelfTest)
                throw new ConfigException("unknown relay flag " + flag + " (see --relay-help)");

            string[] agentArgs = RelayArgs.AgentArgs(args);
            RelayConfig config = RelayConfig.Load(host.ConfigPath);
            ResolvedLaunch r = ProjectResolver.Resolve(config, agent, host.LaunchDir, host.ExePath, host.PathEnv);
            LaunchSpec spec = LaunchSpec.Build(r, agentArgs, host.SystemRoot);
            if (flag == RelayArgs.SelfTest) return SelfTest(host, config, r, spec, rules, agentArgs);

            log = new RelayLog(agent, r.Log, host.ExeDir, stderr);
            log.Write(RelayLog.Basic, "LAUNCH", RelayLog.LaunchText(r, agentArgs, spec));
            IChildProcess child = host.Launcher.Start(spec);
            return Relay(child, host, r, rules, agentArgs, log, stderr, prefix);
        }
        catch (ConfigException e) { return Fail(stderr, log, prefix + e.Message); }
        catch (LaunchException e) { return Fail(stderr, log, prefix + e.Message); }
        finally { log.Close(); }
    }

    static int Relay(IChildProcess child, RelayHost host, ResolvedLaunch r, AgentRules rules, string[] agentArgs,
                     RelayLog log, TextWriter stderr, string prefix)
    {
        var toAgent = new PumpTarget(child.StdIn, log, ToAgent, false);
        var toEditor = new PumpTarget(host.StdOut, log, ToEditor, true);
        var toStderr = new PumpTarget(host.StdErr, log, "AGENT STDERR", true);
        Thread output;
        if (r.IsWsl && rules.ShouldTranslate(agentArgs))
        {
            JsonPathWalker inWalker = Walker(rules, r.Mapper.ToLinux, log, ToAgent);
            JsonPathWalker outWalker = Walker(rules, r.Mapper.ToWindows, log, ToEditor);
            StartThread(() => LinePump.Run(host.StdIn, toAgent, inWalker, log, true));
            output = StartThread(() => LinePump.Run(child.StdOut, toEditor, outWalker, log, false));
        }
        else
        {
            StartThread(() => RawPump.Run(host.StdIn, toAgent, log, RelayLog.Full, ToAgent + " RECEIVED", true));
            output = StartThread(() => RawPump.Run(child.StdOut, toEditor, log, RelayLog.Full, ToEditor + " RECEIVED", false));
        }
        Thread error = StartThread(() => RawPump.Run(child.StdErr, toStderr, log, RelayLog.Basic, "AGENT STDERR", false));

        // Input pump is not joined: stdin may stay open after the child exits.
        child.WaitForExit();
        int code = child.ExitCode;
        try
        {
            child.Cleanup();
            output.Join();
            error.Join();
        }
        catch (Exception e)
        {
            stderr.WriteLine(prefix + "cleanup failed: " + e.Message);
            log.Write(RelayLog.Basic, "ERROR", "cleanup failed: " + e.Message);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            output.Join(Remaining(deadline));
            error.Join(Remaining(deadline));
        }
        try { host.StdOut.Flush(); host.StdErr.Flush(); } catch (Exception) { }
        log.Write(RelayLog.Basic, "EXIT", code.ToString());
        return code;
    }

    static JsonPathWalker Walker(AgentRules rules, Func<string, string> map, RelayLog log, string direction)
    {
        return new JsonPathWalker(rules, map, (field, oldValue, newValue) =>
            log.Write(RelayLog.Changes, direction + " CHANGE",
                      "field=" + field + ": " + JsonPathWalker.Encode(oldValue) + " -> " + JsonPathWalker.Encode(newValue)));
    }

    static Thread StartThread(ThreadStart body)
    {
        var t = new Thread(body);
        t.IsBackground = true;
        t.Start();
        return t;
    }

    static TimeSpan Remaining(DateTime deadline)
    {
        TimeSpan left = deadline - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    static int Fail(TextWriter stderr, RelayLog log, string message)
    {
        stderr.WriteLine(message);
        log.Write(RelayLog.Basic, "ERROR", message);
        return 1;
    }

    static int Print(RelayHost host, string text)
    {
        byte[] b = new UTF8Encoding(false).GetBytes(text + "\n");
        host.StdOut.Write(b, 0, b.Length);
        host.StdOut.Flush();
        return 0;
    }

    static int SelfTest(RelayHost host, RelayConfig config, ResolvedLaunch r, LaunchSpec spec, AgentRules rules,
                        string[] agentArgs)
    {
        var sb = new StringBuilder();
        sb.Append("agent-relay-").Append(rules.Name).Append(' ').Append(RelayVersion.Value).Append(" self-test\n");
        sb.Append("config: ").Append(config.Path).Append('\n');
        sb.Append("launch dir: ").Append(r.LaunchDir).Append('\n');
        sb.Append(r.ProjectRoot != null ? "project: " + r.ProjectRoot : "mode: " + r.Mode).Append('\n');
        if (r.IsWsl)
        {
            sb.Append("distribution: ").Append(r.Distribution).Append('\n');
            sb.Append("linux cwd: ").Append(r.LinuxCwd).Append('\n');
            if (r.Mode == "project")
                foreach (PathMapping m in r.Mapper.Mappings)
                    sb.Append("mapping: ").Append(m.Windows).Append(" <-> ").Append(m.Linux).Append('\n');
        }
        sb.Append("executable: ").Append(r.Executable).Append('\n');
        foreach (var kv in r.Env) sb.Append("env: ").Append(kv.Key).Append('=').Append(RelayLog.Mask(kv.Key, kv.Value)).Append('\n');
        sb.Append("log: level=").Append(r.Log.Level).Append(" dir=").Append(Path.Combine(host.ExeDir, r.Log.Dir))
          .Append(" keep=").Append(r.Log.Keep).Append(" maxSizeMB=").Append(r.Log.MaxSizeMB).Append('\n');
        sb.Append("translate: ").Append(r.IsWsl && rules.ShouldTranslate(agentArgs) ? "yes" : "no").Append('\n');
        sb.Append("command: ").Append(spec.FileName).Append(' ').Append(spec.MaskedArguments).Append('\n');

        bool ok = ArgQuoter.Quote("a b") == "\"a b\"" && ArgQuoter.Quote("x\\\"") == "\"x\\\\\\\"\"" &&
                  ArgQuoter.Quote("") == "\"\"";
        sb.Append("check quoting: ").Append(ok ? "OK" : "FAILED").Append('\n');
        if (r.IsWsl)
        {
            bool roundTrip = string.Equals(r.Mapper.ToWindows(r.LinuxCwd), r.LaunchDir, StringComparison.OrdinalIgnoreCase)
                             && r.LinuxCwd.StartsWith("/");
            sb.Append("check launch dir mapping: ").Append(roundTrip ? "OK" : "FAILED").Append('\n');
            ok = ok && roundTrip;
        }
        sb.Append(ok ? "self-test passed" : "self-test FAILED");
        Print(host, sb.ToString());
        return ok ? 0 : 1;
    }
}
