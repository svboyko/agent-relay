using System;
using System.IO;

// T-C config validation, T-M merge, T-S project selection (SPEC §12.2).
static class ConfigTests
{
    const string Agents = @"'agents':{'claude':{'executable':'/usr/bin/claude'}}";
    const string Base = @"{'defaults':{'distribution':'Ubuntu'," + Agents + @"},
        'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app'}]}";

    static void Error(string json, string part) { T.Throws<ConfigException>(() => T.Config(json), part); }

    static ResolvedLaunch Resolve(string json, string launchDir, string agent = "claude", string pathEnv = "")
    {
        return ProjectResolver.Resolve(T.Config(json), agent, launchDir, "C:\\tools\\agent-relay-" + agent + ".exe", pathEnv);
    }

    static void ResolveError(string json, string launchDir, string part, string agent = "claude", string pathEnv = "")
    {
        T.Throws<ConfigException>(() => Resolve(json, launchDir, agent, pathEnv), part);
    }

    // ---- T-C ----

    public static void MissingFile()
    {
        T.Throws<ConfigException>(() => RelayConfig.Load(Path.Combine(T.TempDir(), "agent-relay.json")), "config not found");
    }

    public static void BadJson() { Error("{'projects': [", "invalid JSON"); }

    public static void BomTolerated()
    {
        string dir = T.TempDir(), path = Path.Combine(dir, "agent-relay.json");
        File.WriteAllText(path, T.J(Base), new System.Text.UTF8Encoding(true));
        T.Equal(1, RelayConfig.Load(path).Projects.Count);
    }

    public static void UnknownFields()
    {
        Error(@"{'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app','distribution':'U','logEnabled':true}]}",
              "projects[0].logEnabled: unknown field");
        Error(@"{'defaults':{'log':{'path':'x'}}," + Base.Substring(Base.IndexOf("'projects'")), "defaults.log.path: unknown field");
        Error(@"{'defaults':{'log':{'enabled':true}}," + Base.Substring(Base.IndexOf("'projects'")), "defaults.log.enabled: unknown field");
        Error(@"{'defaults':{'distribution':'U','agents':{'claude':{'claudePath':'/x'}}},'projects':[]}", "unknown field");
    }

    public static void InvalidLogLevel()
    {
        Error(@"{'defaults':{'distribution':'U','log':{'level':'debug'}},'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w'}]}",
              "defaults.log.level: must be one of");
    }

    public static void WrongTypes()
    {
        Error(@"{'defaults':{'distribution':5}}", "defaults.distribution: must be a string");
        Error(@"{'projects':{}}", "projects: must be an array");
        Error(@"[]", "(root): must be an object");
        Error(@"{'defaults':{'mode':'wsl','distribution':'U','log':{'keep':0}}}", "keep: must be an integer > 0");
        Error(@"{'defaults':{'mode':'wsl','distribution':'U','log':{'keep':2.5}}}", "keep: must be an integer > 0");
        Error(@"{'defaults':{'mode':'wsl','distribution':'U','log':{'maxSizeMB':'1'}}}", "maxSizeMB: must be a number > 0");
        Error(@"{'defaults':{'mode':'wsl','distribution':'U','log':{'maxSizeMB':0}}}", "maxSizeMB: must be a number > 0");
        Error(@"{'defaults':{'mode':'auto'}}", "defaults.mode: must be");
        Error(@"{'defaults':{'mode':'wsl','distribution':'Ubuntu 22'}}", "distribution: must be non-empty");
        Error(@"{'defaults':{'mode':'wsl','distribution':'U','env':{'1A':'x'}}}", "invalid variable name");
    }

    public static void BadRoots()
    {
        Error(@"{'projects':[{'windowsRoot':'app','linuxRoot':'/w','distribution':'U'}]}", "windowsRoot: must be an absolute Windows path");
        Error(@"{'projects':[{'windowsRoot':'C:app','linuxRoot':'/w','distribution':'U'}]}", "windowsRoot: must be an absolute Windows path");
        Error(@"{'projects':[{'windowsRoot':'\\\\server','linuxRoot':'/w','distribution':'U'}]}", "windowsRoot: must be an absolute Windows path");
        Error(@"{'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/','distribution':'U'}]}", "linuxRoot: must be an absolute Linux path");
        Error(@"{'projects':[{'windowsRoot':'C:\\app','linuxRoot':'work','distribution':'U'}]}", "linuxRoot: must be an absolute Linux path");
        Error(@"{'projects':[{'windowsRoot':'C:\\app','distribution':'U'}]}", "linuxRoot: required");
        Error(@"{'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w'}]}", "distribution: required");
    }

    public static void DuplicateRoots()
    {
        Error(@"{'defaults':{'distribution':'U'},'projects':[
                {'windowsRoot':'C:\\App','linuxRoot':'/a'},{'windowsRoot':'c:/app/','linuxRoot':'/b'}]}",
              "projects[1].windowsRoot: duplicate root");
    }

    public static void WindowsPathInExecutable()
    {
        Error(@"{'defaults':{'distribution':'U','agents':{'claude':{'executable':'C:\\claude.exe'}}},'projects':[]}",
              "defaults.agents.claude.executable: must be an absolute Linux path");
    }

    public static void WindowsExecutableOutsideDefaults()
    {
        Error(@"{'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w','distribution':'U',
                'agents':{'claude':{'windowsExecutable':'C:\\c.exe'}}}]}",
              "projects[0].agents.claude.windowsExecutable: unknown field");
        Error(@"{'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w','distribution':'U','mode':'wsl'}]}",
              "projects[0].mode: unknown field");
    }

    public static void MissingAgentExecutable()
    {
        ResolveError(Base, "C:\\app", "project C:\\app has no agents.codex.executable", "codex");
        Resolve(Base, "C:\\app", "claude");  // other agents: shape only
    }

    public static void EmptyProjectsWithModeNone()
    {
        Error(@"{'defaults':{'distribution':'U'}}", "projects: required when defaults.mode is \"none\"");
        Error(@"{'projects':[]}", "projects: required");
        T.Config(@"{'defaults':{'mode':'wsl','distribution':'U'}}");
    }

    static string WindowsModeConfig(string exe)
    {
        string agent = exe == null ? "{}" : "{'windowsExecutable':" + Json(exe) + "}";
        return @"{'defaults':{'mode':'windows','agents':{'claude':" + agent + "}}}";
    }

    static string Json(string s) { return JsonPathWalker.Encode(s).Replace('"', '\''); }

    public static void WindowsModeCmdRejected()
    {
        string dir = T.TempDir(), cmd = Path.Combine(dir, "claude.cmd");
        File.WriteAllText(cmd, "");
        ResolveError(WindowsModeConfig(cmd), "C:\\other", "needs the real .exe");
    }

    public static void WindowsModePathLookup()
    {
        string dir = T.TempDir(), exe = Path.Combine(dir, "claude.exe");
        File.WriteAllText(exe, "");
        ResolvedLaunch r = Resolve(WindowsModeConfig(null), "C:\\other", "claude", "/nonexistent" + Path.PathSeparator + dir);
        T.Equal("windows", r.Mode);
        T.Equal(exe, r.Executable);
        ResolveError(WindowsModeConfig(null), "C:\\other", "claude.exe not found in PATH; set defaults.agents.claude.windowsExecutable", "claude", "/nonexistent");
    }

    public static void WindowsModeExplicitExe()
    {
        string dir = T.TempDir(), exe = Path.Combine(dir, "claude.exe");
        File.WriteAllText(exe, "");
        T.Equal(exe, Resolve(WindowsModeConfig(exe), "C:\\other").Executable);
        ResolveError(WindowsModeConfig(Path.Combine(dir, "missing.exe")), "C:\\other", "not found");
    }

    public static void WindowsModeRejectsRelay()
    {
        string dir = T.TempDir(), exe = Path.Combine(dir, "agent-relay-claude.exe");
        File.WriteAllText(exe, "");
        ResolveError(WindowsModeConfig(exe), "C:\\other", "target is a relay itself");
        string other = Path.Combine(dir, "claude.exe");
        File.WriteAllText(other, "");
        T.Throws<ConfigException>(() => ProjectResolver.Resolve(T.Config(WindowsModeConfig(other)), "claude", "C:\\other", other, ""),
                                  "target is a relay itself");
    }

    public static void ExampleConfigValid()
    {
        string path = Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(T.TestsExe), ".."), "examples"), "agent-relay.json");
        RelayConfig c = RelayConfig.Load(path);
        ResolvedLaunch r = ProjectResolver.Resolve(c, "claude", "C:\\Users\\me\\src\\app\\src", null, "");
        T.Equal("/home/me/src/app/src", r.LinuxCwd);
        ProjectResolver.Resolve(c, "codex", "C:\\Users\\me\\src\\app", null, "");
    }

    // ---- T-M ----

    public static void DistributionFallback()
    {
        T.Equal("Ubuntu", Resolve(Base, "C:\\app").Distribution);
        string own = @"{'defaults':{'distribution':'Ubuntu'," + Agents + @"},
            'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app','distribution':'Debian'}]}";
        T.Equal("Debian", Resolve(own, "C:\\app").Distribution);
    }

    public static void AgentFieldsTakenWhole()
    {
        string json = @"{'defaults':{'distribution':'U','agents':{'claude':{'executable':'/d/claude','env':{'A':'1'}}}},
            'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w','agents':{'claude':{'env':{}}}}]}";
        ResolvedLaunch r = Resolve(json, "C:\\app");
        T.Equal("/d/claude", r.Executable, "executable from defaults agent");
        T.Equal(0, r.Env.Count, "project agent env {} drops default agent env");
    }

    public static void EnvOrder()
    {
        string json = @"{'defaults':{'distribution':'U','env':{'A':'d','B':'d','C':'d'},
                'agents':{'claude':{'executable':'/c','env':{'C':'agent-default'}}}},
            'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w','env':{'B':'p','C':'p'},
                'agents':{'claude':{'env':{'C':'a'}}}}]}";
        ResolvedLaunch r = Resolve(json, "C:\\app");
        T.Equal("d", r.Env["A"]);
        T.Equal("p", r.Env["B"]);
        T.Equal("a", r.Env["C"]);
    }

    public static void LogFieldsMerge()
    {
        string json = @"{'defaults':{'distribution':'U','log':{'dir':'D:\\logs','keep':5},
                'agents':{'claude':{'executable':'/c','log':{'maxSizeMB':3}}}},
            'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w','log':{'keep':7},
                'agents':{'claude':{'log':{'level':'full'}}}}]}";
        EffectiveLog l = Resolve(json, "C:\\app").Log;
        T.Equal("full", l.Level);
        T.Equal("D:\\logs", l.Dir, "dir from defaults kept");
        T.Equal(7, l.Keep);
        T.Equal(10.0, l.MaxSizeMB, "project agent log hides defaults agent log");
    }

    public static void LogDefaults()
    {
        EffectiveLog l = Resolve(Base, "C:\\app").Log;
        T.Equal("off", l.Level);
        T.Equal("logs", l.Dir);
        T.Equal(20, l.Keep);
        T.Equal(10.0, l.MaxSizeMB);
    }

    public static void NullEnvRejected()
    {
        Error(@"{'defaults':{'distribution':'U','env':{'A':null}},'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/w'}]}",
              "defaults.env.A: must be a string");
    }

    public static void MappingConcatAndDuplicates()
    {
        string json = @"{'defaults':{'distribution':'U','pathMappings':[{'windows':'D:\\Shared','linux':'/shared'}]," + Agents + @"},
            'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app','pathMappings':[{'windows':'E:\\x','linux':'/x'}]}]}";
        PathMapper m = Resolve(json, "C:\\app").Mapper;
        T.Equal(3, m.Mappings.Count);
        T.Equal("C:\\app", m.Mappings[0].Windows, "root pair first");
        T.Equal("/shared/a", m.ToLinux("D:\\Shared\\a"));
        T.Equal("/x/b", m.ToLinux("E:\\x\\b"));
        Error(@"{'defaults':{'distribution':'U'},'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app',
                'pathMappings':[{'windows':'E:\\x','linux':'/work/app'}]}]}", "duplicate Linux mapping root /work/app");
        Error(@"{'defaults':{'distribution':'U','pathMappings':[{'windows':'C:\\APP','linux':'/other'}]},
                'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app'}]}", "duplicate Windows mapping root");
    }

    // ---- T-S ----

    const string Nested = @"{'defaults':{'distribution':'U'," + Agents + @"},'projects':[
        {'windowsRoot':'C:\\app','linuxRoot':'/work/app'},
        {'windowsRoot':'C:\\app\\sub','linuxRoot':'/work/sub'},
        {'windowsRoot':'C:\\','linuxRoot':'/mnt/c'},
        {'windowsRoot':'\\\\srv\\share\\proj','linuxRoot':'/srv/proj'}]}";

    static string Root(string launchDir) { return ProjectResolver.Select(T.Config(Nested), launchDir).WindowsRoot; }

    public static void SelectExactSubdirCase()
    {
        T.Equal("C:\\app", Root("C:\\app"));
        T.Equal("C:\\app", Root("C:\\app\\src\\x"));
        T.Equal("C:\\app", Root("c:\\APP\\Src"));
        T.Equal("C:\\app", Root("C:/app/src"));
        T.Equal("/work/app/src/x", Resolve(Nested, "C:\\APP\\src\\x").LinuxCwd);
    }

    public static void SelectBoundary()
    {
        T.Equal("C:", Root("C:\\app-other"), "app-other is not inside app");
        ResolveError(Base, "C:\\app-other", "no project in test.json matches C:\\app-other (defaults.mode is \"none\")");
    }

    public static void SelectLongestWins()
    {
        T.Equal("C:\\app\\sub", Root("C:\\app\\sub\\x"));
        T.Equal("C:\\app\\sub", Root("C:\\app\\sub"));
        T.Equal("C:\\app", Root("C:\\app\\subway"));
    }

    public static void SelectDriveRootAndUnc()
    {
        T.Equal("C:", Root("C:\\anything\\deep"));
        T.Equal("C:", Root("C:\\"));
        T.Equal("/mnt/c/anything", Resolve(Nested, "C:\\anything").LinuxCwd);
        T.Equal("\\\\srv\\share\\proj", Root("\\\\SRV\\share\\proj\\x"));
        T.Equal("/srv/proj/x", Resolve(Nested, "\\\\srv\\share\\proj\\x").LinuxCwd);
        T.True(ProjectResolver.Select(T.Config(Nested), "D:\\app") == null, "D: unmatched");
    }

    public static void NoMatchModeWsl()
    {
        string json = @"{'defaults':{'mode':'wsl','distribution':'U','pathMappings':[{'windows':'D:\\Shared','linux':'/shared'}]," + Agents + @"},
            'projects':[{'windowsRoot':'C:\\app','linuxRoot':'/work/app'}]}";
        ResolvedLaunch r = Resolve(json, "C:\\Users\\me\\x");
        T.Equal("wsl", r.Mode);
        T.Equal("/mnt/c/Users/me/x", r.LinuxCwd);
        T.Equal("/mnt/d/y", r.Mapper.ToLinux("D:\\y"));
        T.Equal("/shared/z", r.Mapper.ToLinux("D:\\Shared\\z"));
        T.Equal("E:\\q", r.Mapper.ToWindows("/mnt/e/q"));
        T.Equal("project", Resolve(json, "C:\\app\\x").Mode);
        ResolveError(json, "\\\\srv\\share", "needs a drive-letter launch dir");
        string custom = @"{'defaults':{'mode':'wsl','automountRoot':'/win/','distribution':'U'," + Agents + "}}";
        T.Equal("/win/c/x", Resolve(custom, "C:\\x").LinuxCwd);
    }
}
