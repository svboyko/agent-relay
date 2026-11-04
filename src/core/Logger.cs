using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Per-session log file (SPEC §10). Never throws: on failure warns once and turns itself off.
public class RelayLog
{
    public const int Off = 0, Basic = 1, Changes = 2, Full = 3;

    static readonly string[] SecretWords = { "KEY", "TOKEN", "SECRET", "PASSWORD", "AUTH", "CREDENTIAL" };

    readonly object sync = new object();
    readonly string agent;
    readonly TextWriter stderr;
    readonly long maxBytes;
    readonly string session = Guid.NewGuid().ToString("N");
    FileStream file;
    long size;

    public int Level { get; private set; }
    public string FilePath { get; private set; }

    public RelayLog(string agent, EffectiveLog settings, string exeDir, TextWriter stderr)
    {
        this.agent = agent;
        this.stderr = stderr;
        Level = Array.IndexOf(RelayConfig.LogLevels, settings.Level);
        maxBytes = (long)(settings.MaxSizeMB * 1024 * 1024);
        if (Level <= Off) return;
        try
        {
            string dir = Path.Combine(exeDir, settings.Dir);
            Directory.CreateDirectory(dir);
            string name = "agent-relay-" + agent + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                          session.Substring(0, 8) + ".log";
            Prune(dir, settings.Keep - 1);
            FilePath = Path.Combine(dir, name);
            file = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        catch (Exception e) { Disable(e.Message); }
    }

    public static RelayLog None(string agent, TextWriter stderr)
    {
        return new RelayLog(agent, new EffectiveLog(), "", stderr);
    }

    public bool IsOn(int level) { return Level >= level && file != null; }

    public void Write(int level, string entry, string payload)
    {
        if (!IsOn(level)) return;
        lock (sync)
        {
            if (file == null) return;
            try
            {
                byte[] bytes = Format(entry, payload);
                if (size + bytes.Length > maxBytes)
                {
                    byte[] limit = Format("LOG LIMIT", "maxSizeMB reached, logging stopped");
                    file.Write(limit, 0, limit.Length);
                    file.Dispose();
                    file = null;
                    return;
                }
                file.Write(bytes, 0, bytes.Length);
                file.Flush();
                size += bytes.Length;
            }
            catch (Exception e) { Disable(e.Message); }
        }
    }

    public void Close()
    {
        lock (sync)
        {
            if (file != null) { try { file.Dispose(); } catch (Exception) { } file = null; }
        }
    }

    byte[] Format(string entry, string payload)
    {
        string text = "[" + DateTime.Now.ToString("o") + " session=" + session + " " + entry + "]\n" +
                      payload + "\n";
        return new UTF8Encoding(false).GetBytes(text);
    }

    void Disable(string message)
    {
        if (file != null) { try { file.Dispose(); } catch (Exception) { } }
        file = null;
        Level = Off;
        try { stderr.WriteLine("agent-relay-" + agent + ": log disabled: " + message); stderr.Flush(); }
        catch (Exception) { }
    }

    // Delete oldest session files until `keep` remain. Best effort: files in use are skipped.
    void Prune(string dir, int keep)
    {
        var files = new List<string>(Directory.GetFiles(dir, "agent-relay-" + agent + "-*.log"));
        files.Sort(StringComparer.Ordinal);  // names sort by time
        for (int i = 0; i < files.Count - keep; i++)
        {
            try { File.Delete(files[i]); } catch (Exception) { }
        }
    }

    public static bool IsSecret(string name)
    {
        string up = name.ToUpperInvariant();
        foreach (string w in SecretWords) if (up.Contains(w)) return true;
        return false;
    }

    public static string Mask(string name, string value)
    {
        return IsSecret(name) ? "<masked, " + value.Length + " chars>" : value;
    }

    // LAUNCH entry payload (SPEC §10.3).
    public static string LaunchText(ResolvedLaunch r, string[] agentArgs, LaunchSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("version: ").Append(RelayVersion.Value).Append('\n');
        sb.Append("launch dir: ").Append(r.LaunchDir).Append('\n');
        sb.Append(r.ProjectRoot != null ? "project: " + r.ProjectRoot : "mode: " + r.Mode).Append('\n');
        sb.Append("agent args:\n");
        foreach (string a in agentArgs) sb.Append("  ").Append(a).Append('\n');
        sb.Append("command: ").Append(spec.FileName).Append(' ').Append(spec.MaskedArguments).Append('\n');
        sb.Append("effective env:\n");
        foreach (var kv in r.Env) sb.Append("  ").Append(kv.Key).Append('=').Append(Mask(kv.Key, kv.Value)).Append('\n');
        sb.Append("process env:\n");
        var names = new List<string>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) names.Add((string)e.Key);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string n in names)
            sb.Append("  ").Append(n).Append('=').Append(Mask(n, Environment.GetEnvironmentVariable(n) ?? "")).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }
}
