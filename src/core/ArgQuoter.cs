using System.Collections.Generic;
using System.Text;

// Windows command-line quoting, inverse of CommandLineToArgvW.
public static class ArgQuoter
{
    public static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1);
            else sb.Append('\\', slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        return sb.Append('"').ToString();
    }

    public static string Join(IEnumerable<string> args)
    {
        var parts = new List<string>();
        foreach (string a in args) parts.Add(Quote(a));
        return string.Join(" ", parts);
    }
}
