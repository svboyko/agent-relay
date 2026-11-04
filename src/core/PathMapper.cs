using System;
using System.Collections.Generic;

public class PathMapping
{
    public string Windows;  // normalized: '\' separators, no trailing '\' ("C:" for a drive root)
    public string Linux;    // normalized: no trailing '/'

    public PathMapping(string windows, string linux) { Windows = windows; Linux = linux; }
}

// Bidirectional longest-prefix path mapping (SPEC §8.1).
public class PathMapper
{
    readonly List<PathMapping> mappings;

    public PathMapper(List<PathMapping> mappings) { this.mappings = mappings; }

    public List<PathMapping> Mappings { get { return mappings; } }

    // Returns the value unchanged when it does not start with a mapped root.
    public string ToLinux(string value)
    {
        if (!LooksLikeWindowsPath(value)) return value;
        string v = value.Replace('/', '\\');
        PathMapping best = null;
        foreach (PathMapping m in mappings)
            if (HasRoot(v, m.Windows, '\\', StringComparison.OrdinalIgnoreCase) &&
                (best == null || m.Windows.Length > best.Windows.Length))
                best = m;
        if (best == null) return value;
        return best.Linux + v.Substring(best.Windows.Length).Replace('\\', '/');
    }

    public string ToWindows(string value)
    {
        if (value.Length == 0 || value[0] != '/' || HasNewline(value)) return value;
        PathMapping best = null;
        foreach (PathMapping m in mappings)
            if (HasRoot(value, m.Linux, '/', StringComparison.Ordinal) &&
                (best == null || m.Linux.Length > best.Linux.Length))
                best = m;
        if (best == null) return value;
        string result = best.Windows + value.Substring(best.Linux.Length).Replace('/', '\\');
        return IsBareDrive(result) ? result + "\\" : result;
    }

    static bool HasRoot(string value, string root, char sep, StringComparison cmp)
    {
        if (!value.StartsWith(root, cmp)) return false;
        return value.Length == root.Length || value[root.Length] == sep;
    }

    static bool HasNewline(string s) { return s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0; }

    static bool IsLetter(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); }

    public static bool IsBareDrive(string s) { return s.Length == 2 && IsLetter(s[0]) && s[1] == ':'; }

    // C:\x, C:/x, C: or \\server...
    static bool LooksLikeWindowsPath(string s)
    {
        if (HasNewline(s)) return false;
        if (IsBareDrive(s)) return true;
        if (s.Length >= 3 && IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) return true;
        return s.StartsWith("\\\\");
    }

    // Drive-absolute (C:\..., C:/...) or UNC (\\server\share...).
    public static bool IsWindowsAbsolute(string s)
    {
        if (s == null || HasNewline(s)) return false;
        if (s.Length >= 3 && IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) return true;
        string u = s.Replace('/', '\\');
        if (!u.StartsWith("\\\\")) return false;
        string[] parts = u.Substring(2).Split('\\');
        return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0;
    }

    public static string NormalizeWindows(string s)
    {
        string n = s.Replace('/', '\\');
        while (n.EndsWith("\\") && n.Length > 2) n = n.Substring(0, n.Length - 1);
        return n;
    }

    // Absolute and not "/".
    public static bool IsLinuxAbsolute(string s)
    {
        return s != null && s.StartsWith("/") && !HasNewline(s) && NormalizeLinux(s).Length > 0;
    }

    public static string NormalizeLinux(string s) { return s.TrimEnd('/'); }
}
