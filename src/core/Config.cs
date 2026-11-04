using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

public class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}

// Log settings; null fields = not set at this level.
public class LogConfig
{
    public string Level;
    public string Dir;
    public int? Keep;
    public double? MaxSizeMB;
}

public class AgentConfig
{
    public string Executable;
    public string WindowsExecutable;  // defaults only
    public Dictionary<string, string> Env;
    public LogConfig Log;
}

// Fields shared by "defaults" and "projects[]".
public class SectionConfig
{
    public string Distribution;
    public List<PathMapping> PathMappings = new List<PathMapping>();
    public Dictionary<string, string> Env = new Dictionary<string, string>();
    public LogConfig Log = new LogConfig();
    public Dictionary<string, AgentConfig> Agents = new Dictionary<string, AgentConfig>();
}

public class DefaultsConfig : SectionConfig
{
    public string Mode = "none";
    public string AutomountRoot = "/mnt";
}

public class ProjectConfig : SectionConfig
{
    public string WindowsRoot;
    public string LinuxRoot;
}

// agent-relay.json, loaded and validated (SPEC §4).
public class RelayConfig
{
    public string Path;
    public DefaultsConfig Defaults = new DefaultsConfig();
    public List<ProjectConfig> Projects = new List<ProjectConfig>();

    public static readonly string[] LogLevels = { "off", "basic", "changes", "full" };
    static readonly Regex EnvName = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");

    public static RelayConfig Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException("config not found: " + path);
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception e) { throw new ConfigException("cannot read " + path + ": " + e.Message); }
        return Parse(text, path);
    }

    public static RelayConfig Parse(string text, string path)
    {
        object root;
        try { root = new JavaScriptSerializer().DeserializeObject(text.TrimStart('\uFEFF')); }
        catch (Exception e) { throw new ConfigException(path + ": invalid JSON: " + e.Message); }

        var c = new RelayConfig();
        c.Path = path;
        try { c.Read(root); }
        catch (ConfigException e) { throw new ConfigException(path + ": " + e.Message); }
        return c;
    }

    void Read(object root)
    {
        var obj = Obj(root, "(root)");
        Allow(obj, "", "defaults", "projects");

        if (obj.ContainsKey("defaults"))
        {
            var d = Obj(obj["defaults"], "defaults");
            Allow(d, "defaults.", "mode", "distribution", "automountRoot", "pathMappings", "env", "log", "agents");
            ReadSection(d, "defaults.", Defaults, true);
            if (d.ContainsKey("mode"))
            {
                Defaults.Mode = Str(d["mode"], "defaults.mode");
                if (Array.IndexOf(new[] { "none", "wsl", "windows" }, Defaults.Mode) < 0)
                    throw new ConfigException("defaults.mode: must be \"none\", \"wsl\" or \"windows\"");
            }
            if (d.ContainsKey("automountRoot"))
                Defaults.AutomountRoot = LinuxPath(d["automountRoot"], "defaults.automountRoot");
        }

        if (obj.ContainsKey("projects"))
        {
            object[] arr = Arr(obj["projects"], "projects");
            for (int i = 0; i < arr.Length; i++)
            {
                string at = "projects[" + i + "]";
                var p = Obj(arr[i], at);
                Allow(p, at + ".", "windowsRoot", "linuxRoot", "distribution", "pathMappings", "env", "log", "agents");
                var proj = new ProjectConfig();
                if (!p.ContainsKey("windowsRoot")) throw new ConfigException(at + ".windowsRoot: required");
                if (!p.ContainsKey("linuxRoot")) throw new ConfigException(at + ".linuxRoot: required");
                proj.WindowsRoot = WindowsPath(p["windowsRoot"], at + ".windowsRoot");
                proj.LinuxRoot = LinuxPath(p["linuxRoot"], at + ".linuxRoot");
                ReadSection(p, at + ".", proj, false);
                if (proj.Distribution == null && Defaults.Distribution == null)
                    throw new ConfigException(at + ".distribution: required (or set defaults.distribution)");
                CheckDuplicates(MappingsFor(proj), at);
                foreach (ProjectConfig other in Projects)
                    if (string.Equals(other.WindowsRoot, proj.WindowsRoot, StringComparison.OrdinalIgnoreCase))
                        throw new ConfigException(at + ".windowsRoot: duplicate root " + proj.WindowsRoot);
                Projects.Add(proj);
            }
        }

        if (Projects.Count == 0 && Defaults.Mode == "none")
            throw new ConfigException("projects: required when defaults.mode is \"none\"");
        CheckDuplicates(Defaults.PathMappings, "defaults");
    }

    void ReadSection(Dictionary<string, object> o, string at, SectionConfig s, bool isDefaults)
    {
        if (o.ContainsKey("distribution"))
        {
            s.Distribution = Str(o["distribution"], at + "distribution");
            if (s.Distribution.Length == 0 || s.Distribution.IndexOfAny(new[] { ' ', '\t', '"', '\'' }) >= 0)
                throw new ConfigException(at + "distribution: must be non-empty, without whitespace or quotes");
        }
        if (o.ContainsKey("pathMappings"))
        {
            object[] arr = Arr(o["pathMappings"], at + "pathMappings");
            for (int i = 0; i < arr.Length; i++)
            {
                string mat = at + "pathMappings[" + i + "]";
                var m = Obj(arr[i], mat);
                Allow(m, mat + ".", "windows", "linux");
                if (!m.ContainsKey("windows") || !m.ContainsKey("linux"))
                    throw new ConfigException(mat + ": \"windows\" and \"linux\" are required");
                s.PathMappings.Add(new PathMapping(WindowsPath(m["windows"], mat + ".windows"),
                                                   LinuxPath(m["linux"], mat + ".linux")));
            }
        }
        if (o.ContainsKey("env")) s.Env = Env(o["env"], at + "env");
        if (o.ContainsKey("log")) s.Log = Log(o["log"], at + "log");
        if (o.ContainsKey("agents"))
        {
            var agents = Obj(o["agents"], at + "agents");
            foreach (var kv in agents)
            {
                string aat = at + "agents." + kv.Key;
                var a = Obj(kv.Value, aat);
                if (isDefaults) Allow(a, aat + ".", "executable", "windowsExecutable", "env", "log");
                else Allow(a, aat + ".", "executable", "env", "log");
                var ac = new AgentConfig();
                if (a.ContainsKey("executable"))
                {
                    ac.Executable = Str(a["executable"], aat + ".executable");
                    if (!ac.Executable.StartsWith("/") || ac.Executable.Length < 2)
                        throw new ConfigException(aat + ".executable: must be an absolute Linux path");
                }
                if (a.ContainsKey("windowsExecutable"))
                {
                    ac.WindowsExecutable = Str(a["windowsExecutable"], aat + ".windowsExecutable");
                    if (ac.WindowsExecutable.Length == 0)
                        throw new ConfigException(aat + ".windowsExecutable: must not be empty");
                }
                if (a.ContainsKey("env")) ac.Env = Env(a["env"], aat + ".env");
                if (a.ContainsKey("log")) ac.Log = Log(a["log"], aat + ".log");
                s.Agents[kv.Key] = ac;
            }
        }
    }

    // Project root pair first, then defaults + project pathMappings.
    public List<PathMapping> MappingsFor(ProjectConfig p)
    {
        var list = new List<PathMapping>();
        list.Add(new PathMapping(p.WindowsRoot, p.LinuxRoot));
        list.AddRange(Defaults.PathMappings);
        list.AddRange(p.PathMappings);
        return list;
    }

    static void CheckDuplicates(List<PathMapping> list, string at)
    {
        for (int i = 0; i < list.Count; i++)
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(list[i].Windows, list[j].Windows, StringComparison.OrdinalIgnoreCase))
                    throw new ConfigException(at + ": duplicate Windows mapping root " + list[i].Windows);
                if (list[i].Linux == list[j].Linux)
                    throw new ConfigException(at + ": duplicate Linux mapping root " + list[i].Linux);
            }
    }

    static LogConfig Log(object v, string at)
    {
        var o = Obj(v, at);
        Allow(o, at + ".", "level", "dir", "keep", "maxSizeMB");
        var l = new LogConfig();
        if (o.ContainsKey("level"))
        {
            l.Level = Str(o["level"], at + ".level");
            if (Array.IndexOf(LogLevels, l.Level) < 0)
                throw new ConfigException(at + ".level: must be one of off, basic, changes, full");
        }
        if (o.ContainsKey("dir"))
        {
            l.Dir = Str(o["dir"], at + ".dir");
            if (l.Dir.Length == 0) throw new ConfigException(at + ".dir: must not be empty");
        }
        if (o.ContainsKey("keep"))
        {
            object k = o["keep"];
            if (!(k is int) || (int)k <= 0) throw new ConfigException(at + ".keep: must be an integer > 0");
            l.Keep = (int)k;
        }
        if (o.ContainsKey("maxSizeMB"))
        {
            object m = o["maxSizeMB"];
            double d;
            if (m is int) d = (int)m;
            else if (m is long) d = (long)m;
            else if (m is decimal) d = (double)(decimal)m;
            else if (m is double) d = (double)m;
            else throw new ConfigException(at + ".maxSizeMB: must be a number > 0");
            if (d <= 0) throw new ConfigException(at + ".maxSizeMB: must be a number > 0");
            l.MaxSizeMB = d;
        }
        return l;
    }

    static Dictionary<string, string> Env(object v, string at)
    {
        var o = Obj(v, at);
        var env = new Dictionary<string, string>();
        foreach (var kv in o)
        {
            if (!EnvName.IsMatch(kv.Key)) throw new ConfigException(at + "." + kv.Key + ": invalid variable name");
            env[kv.Key] = Str(kv.Value, at + "." + kv.Key);
        }
        return env;
    }

    static string WindowsPath(object v, string at)
    {
        string s = Str(v, at);
        if (!PathMapper.IsWindowsAbsolute(s))
            throw new ConfigException(at + ": must be an absolute Windows path (C:\\... or \\\\server\\share)");
        return PathMapper.NormalizeWindows(s);
    }

    static string LinuxPath(object v, string at)
    {
        string s = Str(v, at);
        if (!PathMapper.IsLinuxAbsolute(s)) throw new ConfigException(at + ": must be an absolute Linux path, not /");
        return PathMapper.NormalizeLinux(s);
    }

    static Dictionary<string, object> Obj(object v, string at)
    {
        var d = v as Dictionary<string, object>;
        if (d == null) throw new ConfigException(at + ": must be an object");
        return d;
    }

    static object[] Arr(object v, string at)
    {
        var a = v as object[];
        if (a == null) throw new ConfigException(at + ": must be an array");
        return a;
    }

    static string Str(object v, string at)
    {
        var s = v as string;
        if (s == null) throw new ConfigException(at + ": must be a string");
        return s;
    }

    static void Allow(Dictionary<string, object> o, string prefix, params string[] names)
    {
        foreach (string key in o.Keys)
            if (Array.IndexOf(names, key) < 0) throw new ConfigException(prefix + key + ": unknown field");
    }
}
