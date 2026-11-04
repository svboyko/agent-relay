using System;
using System.IO;
using System.Runtime.InteropServices;

// T-Q quoting, T-A relay args, T-L launch spec, T-F past failures (launch part) (SPEC §12.2).
static class LaunchTests
{
    // ---- T-Q ----

    public static void QuoteCases()
    {
        T.Equal("abc", ArgQuoter.Quote("abc"));
        T.Equal("DenwAP74", ArgQuoter.Quote("DenwAP74"), "distro unquoted");
        T.Equal("/home/me/.local/bin/claude", ArgQuoter.Quote("/home/me/.local/bin/claude"));
        T.Equal("\"\"", ArgQuoter.Quote(""));
        T.Equal("\"two words\"", ArgQuoter.Quote("two words"));
        T.Equal("\"a\\\"b\"", ArgQuoter.Quote("a\"b"));
        T.Equal("\"two words\\\\\"", ArgQuoter.Quote("two words\\"));
        T.Equal("C:\\x\\", ArgQuoter.Quote("C:\\x\\"), "no quoting → backslashes literal");
        T.Equal("\"a\\\\\\\"b\"", ArgQuoter.Quote("a\\\"b"));
        T.Equal("\"C:\\a b\\x\"", ArgQuoter.Quote("C:\\a b\\x"), "inner backslashes not doubled");
        T.Equal("café", ArgQuoter.Quote("café"));
        T.Equal("\"tab\there\"", ArgQuoter.Quote("tab\there"));
    }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    public static void QuoteRoundTripWindows()
    {
        T.WindowsOnly();
        string[] args = { "", "a b", "a\"b", "a\\\"b", "x\\", "x y\\", "x y\\\\", "\\\\srv\\s h", "\"", "\\\"\\",
                          "café ü", "$HOME;x", "it's", "*", "`id`", "a\tb", "  " };
        string line = "prog " + ArgQuoter.Join(args);
        int argc;
        IntPtr argv = CommandLineToArgvW(line, out argc);
        try
        {
            T.Equal(args.Length + 1, argc, "argc for " + line);
            for (int i = 0; i < args.Length; i++)
                T.Equal(args[i], Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, (i + 1) * IntPtr.Size)), "arg " + i);
        }
        finally { LocalFree(argv); }
    }

    // ---- T-A ----

    // Runs the relay in-process with memory streams; returns exit code.
    static int RunRelay(string configJson, string launchDir, string[] args, out string stdout, out string stderr)
    {
        string dir = T.TempDir();
        if (configJson != null) File.WriteAllText(Path.Combine(dir, "agent-relay.json"), T.J(configJson));
        var host = new RelayHost();
        host.ExeDir = dir;
        host.ExePath = Path.Combine(dir, "agent-relay-claude.exe");
        host.ConfigPath = Path.Combine(dir, "agent-relay.json");
        host.LaunchDir = launchDir;
        host.PathEnv = "";
        host.SystemRoot = "C:\\Windows";
        host.StdIn = new MemoryStream();
        var o = new MemoryStream();
        var e = new MemoryStream();
        host.StdOut = o;
        host.StdErr = e;
        host.Launcher = null;  // must not be reached
        int code = RelayMain.Run("claude", new ClaudeRules(), args, host);
        stdout = T.Utf8(o.ToArray());
        stderr = T.Utf8(e.ToArray());
        return code;
    }

    const string Config = @"{'defaults':{'distribution':'DenwAP74','env':{'API_KEY':'secret','MODE':'x'},
        'agents':{'claude':{'executable':'/usr/bin/claude'}}},
        'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app'}]}";

    public static void RelayVersionFlag()
    {
        string o, e;
        T.Equal(0, RunRelay(null, "C:\\app", new[] { "--relay-version" }, out o, out e));
        T.Equal("agent-relay-claude " + RelayVersion.Value + "\n", o);
        T.Equal("", e);
    }

    public static void RelayHelpFlag()
    {
        string o, e;
        T.Equal(0, RunRelay(null, "C:\\app", new[] { "--relay-help" }, out o, out e));
        T.Contains("--relay-self-test", o);
    }

    public static void RelaySelfTest()
    {
        string o, e;
        T.Equal(0, RunRelay(Config, "C:\\app\\src", new[] { "--relay-self-test", "--output-format", "stream-json" }, out o, out e), e + o);
        T.Contains("project: C:\\app", o);
        T.Contains("linux cwd: /work/app/src", o);
        T.Contains("translate: yes", o);
        T.Contains("command: C:\\Windows" + Path.DirectorySeparatorChar + "System32" + Path.DirectorySeparatorChar +
                   "wsl.exe -d DenwAP74 --cd /work/app/src --exec env \"API_KEY=<masked, 6 chars>\" MODE=x /usr/bin/claude --output-format stream-json", o);
        T.NotContains("secret", o);
        T.Contains("self-test passed", o);
    }

    public static void RelaySelfTestConfigError()
    {
        string o, e;
        T.Equal(1, RunRelay(Config, "C:\\elsewhere", new[] { "--relay-self-test" }, out o, out e));
        T.Contains("agent-relay-claude: no project in ", e);
        T.Equal("", o);
    }

    public static void UnknownRelayFlag()
    {
        string o, e;
        T.Equal(1, RunRelay(Config, "C:\\app", new[] { "--relay-x" }, out o, out e));
        T.Equal("agent-relay-claude: unknown relay flag --relay-x (see --relay-help)" + Environment.NewLine, e);
        T.Equal("", o);
    }

    public static void ConfigErrorNoChild()
    {
        string o, e;
        T.Equal(1, RunRelay(null, "C:\\app", new[] { "-p", "hi" }, out o, out e));
        T.Contains("agent-relay-claude: config not found: ", e);
        T.Equal("", o);
    }

    public static void AgentFlagsForwarded()
    {
        T.True(RelayArgs.Flag(new[] { "--version" }) == null, "--version is not a relay flag");
        T.True(RelayArgs.Flag(new[] { "-p", "--relay-version" }) == null, "relay flags only first");
        T.Equal("--version", RelayArgs.AgentArgs(new[] { "--version" })[0]);
        T.Equal(1, RelayArgs.AgentArgs(new[] { "--relay-self-test", "x" }).Length);
    }

    // ---- T-L ----

    static ResolvedLaunch Wsl(string launchDir, string envJson)
    {
        string json = @"{'defaults':{'distribution':'Ubuntu','env':" + envJson + @",'agents':{'claude':{'executable':'/usr/bin/claude'}}},
            'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app'}]}";
        return ProjectResolver.Resolve(T.Config(json), "claude", launchDir, null, "");
    }

    public static void SpecNoEnv()
    {
        LaunchSpec s = LaunchSpec.Build(Wsl("C:\\app", "{}"), new[] { "--output-format", "stream-json" }, "C:\\Windows");
        T.Equal(Path.Combine("C:\\Windows", "System32", "wsl.exe"), s.FileName);
        T.Equal("-d Ubuntu --cd /work/app --exec /usr/bin/claude --output-format stream-json", s.Arguments);
        T.Equal("C:\\app", s.WorkingDirectory);
        T.Equal("1", s.Environment["WSL_UTF8"], "WSL_UTF8=1 for wsl.exe (past failure)");
        T.Equal(1, s.Environment.Count, "no WSLENV, env goes through env K=V");
    }

    public static void SpecWithEnv()
    {
        LaunchSpec s = LaunchSpec.Build(Wsl("C:\\app", "{'A':'1','B':'x y'}"), new[] { "-p", "it's \"q\"" }, "C:\\Windows");
        T.Equal("-d Ubuntu --cd /work/app --exec env A=1 \"B=x y\" /usr/bin/claude -p \"it's \\\"q\\\"\"", s.Arguments);
    }

    public static void SpecSpacesInCwd()
    {
        LaunchSpec s = LaunchSpec.Build(Wsl("C:\\app\\my dir", "{}"), new string[0], "C:\\Windows");
        T.Equal("-d Ubuntu --cd \"/work/app/my dir\" --exec /usr/bin/claude", s.Arguments);
    }

    public static void SpecModeWsl()
    {
        string json = @"{'defaults':{'mode':'wsl','distribution':'Ubuntu','agents':{'claude':{'executable':'/usr/bin/claude'}}}}";
        ResolvedLaunch r = ProjectResolver.Resolve(T.Config(json), "claude", "D:\\Work\\x", null, "");
        T.Equal("-d Ubuntu --cd /mnt/d/Work/x --exec /usr/bin/claude", LaunchSpec.Build(r, new string[0], "C:\\Windows").Arguments);
    }

    public static void SpecModeWindows()
    {
        string dir = T.TempDir(), exe = Path.Combine(dir, "claude.exe");
        File.WriteAllText(exe, "");
        string json = @"{'defaults':{'mode':'windows','env':{'X':'1'}}}";
        ResolvedLaunch r = ProjectResolver.Resolve(T.Config(json), "claude", "C:\\other", null, dir);
        LaunchSpec s = LaunchSpec.Build(r, new[] { "auth", "a b" }, "C:\\Windows");
        T.Equal(exe, s.FileName);
        T.Equal("auth \"a b\"", s.Arguments);
        T.Equal("1", s.Environment["X"]);
        T.True(!s.Environment.ContainsKey("WSL_UTF8"), "no WSL_UTF8 in mode windows");
    }

    public static void SpecMasking()
    {
        LaunchSpec s = LaunchSpec.Build(Wsl("C:\\app", "{'API_KEY':'secret','GH_TOKEN':'t','Auth_x':'ab','PLAIN':'v'}"),
                                        new string[0], "C:\\Windows");
        T.Contains("API_KEY=secret", s.Arguments);
        T.Equal("-d Ubuntu --cd /work/app --exec env \"API_KEY=<masked, 6 chars>\" \"GH_TOKEN=<masked, 1 chars>\" " +
                "\"Auth_x=<masked, 2 chars>\" PLAIN=v /usr/bin/claude", s.MaskedArguments);
        T.True(RelayLog.IsSecret("my_password") && RelayLog.IsSecret("CREDENTIALS") && !RelayLog.IsSecret("PATH"), "secret names");
    }
}
