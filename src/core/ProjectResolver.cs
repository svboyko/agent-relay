using System;
using System.Collections.Generic;
using System.IO;

public class EffectiveLog
{
    public string Level = "off";
    public string Dir = "logs";   // absolute after resolve
    public int Keep = 20;
    public double MaxSizeMB = 10;
}

// Everything needed to launch one agent session.
public class ResolvedLaunch
{
    public string Mode;          // "project" | "wsl" | "windows"
    public string ProjectRoot;   // matched windowsRoot, or null
    public string LaunchDir;
    public string Distribution;  // WSL modes
    public string LinuxCwd;      // WSL modes
    public string Executable;    // Linux path (WSL modes) or full Windows .exe path (mode windows)
    public PathMapper Mapper;    // WSL modes
    public Dictionary<string, string> Env = new Dictionary<string, string>();
    public EffectiveLog Log = new EffectiveLog();

    public bool IsWsl { get { return Mode != "windows"; } }
}

// Launch dir → project or default mode, with merged settings (SPEC §4.3–4.6).
public static class ProjectResolver
{
    public static ProjectConfig Select(RelayConfig config, string launchDir)
    {
        string dir = PathMapper.NormalizeWindows(launchDir);
        ProjectConfig best = null;
        foreach (ProjectConfig p in config.Projects)
        {
            bool match = string.Equals(dir, p.WindowsRoot, StringComparison.OrdinalIgnoreCase) ||
                         dir.StartsWith(p.WindowsRoot + "\\", StringComparison.OrdinalIgnoreCase);
            if (match && (best == null || p.WindowsRoot.Length > best.WindowsRoot.Length)) best = p;
        }
        return best;
    }

    // selfPath = relay exe (mode windows guard); pathEnv = PATH value (mode windows lookup).
    public static ResolvedLaunch Resolve(RelayConfig config, string agent, string launchDir,
                                         string selfPath, string pathEnv)
    {
        DefaultsConfig d = config.Defaults;
        var r = new ResolvedLaunch();
        r.LaunchDir = PathMapper.NormalizeWindows(launchDir);
        ProjectConfig p = Select(config, launchDir);

        if (p != null)
        {
            r.Mode = "project";
            r.ProjectRoot = p.WindowsRoot;
            r.Distribution = p.Distribution ?? d.Distribution;
            r.Mapper = new PathMapper(config.MappingsFor(p));
            AgentConfig pa = Agent(p, agent), da = Agent(d, agent);
            r.Executable = pa.Executable ?? da.Executable;
            if (r.Executable == null)
                throw new ConfigException("project " + p.WindowsRoot + " has no agents." + agent + ".executable");
            Merge(r, d, p, Pick(pa, da));
        }
        else if (d.Mode == "wsl")
        {
            r.Mode = "wsl";
            if (!PathMapper.IsWindowsAbsolute(launchDir) || r.LaunchDir.StartsWith("\\\\"))
                throw new ConfigException("defaults.mode \"wsl\" needs a drive-letter launch dir, got " + launchDir);
            r.Distribution = d.Distribution;
            if (r.Distribution == null) throw new ConfigException("defaults.distribution: required for mode \"wsl\"");
            var maps = new List<PathMapping>();
            for (char c = 'a'; c <= 'z'; c++)
                maps.Add(new PathMapping(char.ToUpperInvariant(c) + ":", d.AutomountRoot + "/" + c));
            maps.AddRange(d.PathMappings);
            r.Mapper = new PathMapper(maps);
            r.Executable = Agent(d, agent).Executable;
            if (r.Executable == null) throw new ConfigException("defaults has no agents." + agent + ".executable");
            Merge(r, d, null, Agent(d, agent));
        }
        else if (d.Mode == "windows")
        {
            r.Mode = "windows";
            r.Executable = FindWindowsExe(Agent(d, agent).WindowsExecutable, agent, selfPath, pathEnv);
            Merge(r, d, null, Agent(d, agent));
        }
        else
        {
            throw new ConfigException("no project in " + config.Path + " matches " + launchDir +
                                      " (defaults.mode is \"none\")");
        }

        if (r.Mapper != null) r.LinuxCwd = r.Mapper.ToLinux(r.LaunchDir);
        return r;
    }

    static AgentConfig Agent(SectionConfig s, string agent)
    {
        AgentConfig a;
        return s.Agents.TryGetValue(agent, out a) ? a : new AgentConfig();
    }

    // Agent fields taken whole: project agent value, else defaults agent value (D12).
    static AgentConfig Pick(AgentConfig project, AgentConfig defaults)
    {
        var a = new AgentConfig();
        a.Env = project.Env ?? defaults.Env;
        a.Log = project.Log ?? defaults.Log;
        return a;
    }

    // env and log: defaults → project → agent, later wins per key/field.
    static void Merge(ResolvedLaunch r, DefaultsConfig d, ProjectConfig p, AgentConfig agent)
    {
        var envs = new List<Dictionary<string, string>> { d.Env, p != null ? p.Env : null, agent.Env };
        foreach (var env in envs)
            if (env != null)
                foreach (var kv in env) r.Env[kv.Key] = kv.Value;

        var logs = new List<LogConfig> { d.Log, p != null ? p.Log : null, agent.Log };
        foreach (LogConfig l in logs)
        {
            if (l == null) continue;
            if (l.Level != null) r.Log.Level = l.Level;
            if (l.Dir != null) r.Log.Dir = l.Dir;
            if (l.Keep != null) r.Log.Keep = l.Keep.Value;
            if (l.MaxSizeMB != null) r.Log.MaxSizeMB = l.MaxSizeMB.Value;
        }
    }

    static string FindWindowsExe(string configured, string agent, string selfPath, string pathEnv)
    {
        string exe = null;
        if (configured != null)
        {
            exe = Environment.ExpandEnvironmentVariables(configured);
            if (!Path.IsPathRooted(exe))
                throw new ConfigException("defaults.agents." + agent + ".windowsExecutable: must be an absolute path");
            if (!File.Exists(exe))
                throw new ConfigException("defaults.agents." + agent + ".windowsExecutable: not found: " + exe);
        }
        else
        {
            foreach (string dir in (pathEnv ?? "").Split(Path.PathSeparator))
            {
                if (dir.Trim().Length == 0) continue;
                string candidate;
                try { candidate = Path.Combine(dir.Trim().Trim('"'), agent + ".exe"); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) { exe = candidate; break; }
            }
            if (exe == null)
                throw new ConfigException(agent + ".exe not found in PATH; set defaults.agents." + agent +
                                          ".windowsExecutable");
        }

        exe = Path.GetFullPath(exe);
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ConfigException("mode \"windows\" needs the real .exe, not " + exe);
        bool isSelf = selfPath != null &&
                      string.Equals(Path.GetFullPath(selfPath), exe, StringComparison.OrdinalIgnoreCase);
        if (isSelf || Path.GetFileName(exe).StartsWith("agent-relay-", StringComparison.OrdinalIgnoreCase))
            throw new ConfigException("mode \"windows\" target is a relay itself: " + exe);
        return exe;
    }
}
