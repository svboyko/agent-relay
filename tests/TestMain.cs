using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

// Test runner (SPEC §12.1). Runs every public static void method of classes named *Tests.
// Also hosts the fake child (--fake-child) and the out-of-process relay (--relay-under-test).
static class TestMain
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake-child") return FakeChild.Run(args);
        if (args.Length > 0 && args[0] == "--relay-under-test") return LifecycleTests.RelayUnderTest(args);

        string filter = args.Length > 0 ? args[0] : null;
        int pass = 0, fail = 0, skip = 0;
        var failed = new List<string>();
        var types = new List<Type>(Assembly.GetExecutingAssembly().GetTypes());
        types.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (Type type in types)
        {
            if (!type.Name.EndsWith("Tests")) continue;
            var methods = new List<MethodInfo>(type.GetMethods(BindingFlags.Public | BindingFlags.Static));
            methods.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            foreach (MethodInfo m in methods)
            {
                if (m.ReturnType != typeof(void) || m.GetParameters().Length != 0) continue;
                string name = type.Name + "." + m.Name;
                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string error = RunOne(m);
                if (error == null) { pass++; Console.WriteLine("PASS " + name); }
                else if (error.StartsWith("SKIP")) { skip++; Console.WriteLine("SKIP " + name + error.Substring(4)); }
                else { fail++; failed.Add(name); Console.WriteLine("FAIL " + name + "\n     " + error.Replace("\n", "\n     ")); }
            }
        }
        Console.WriteLine();
        Console.WriteLine("passed " + pass + ", failed " + fail + ", skipped " + skip);
        foreach (string f in failed) Console.WriteLine("  failed: " + f);
        return fail == 0 && pass > 0 ? 0 : 1;
    }

    // null = pass; "SKIP ..." = skipped; else failure text. Every test has a 10 s timeout.
    static string RunOne(MethodInfo m)
    {
        string error = null;
        var t = new Thread(() =>
        {
            try { m.Invoke(null, null); }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException;
                error = inner is SkipException ? "SKIP " + inner.Message : inner.GetType().Name + ": " + inner.Message +
                        (inner is AssertException ? "" : "\n" + inner.StackTrace);
            }
        });
        t.IsBackground = true;
        t.Start();
        if (!t.Join(10000)) return "timeout (10 s)";
        return error;
    }
}

class AssertException : Exception { public AssertException(string m) : base(m) { } }
class SkipException : Exception { public SkipException(string m) : base(m) { } }

static class T
{
    public static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
    public static bool IsMono { get { return Type.GetType("Mono.Runtime") != null; } }

    public static void WindowsOnly()
    {
        if (!IsWindows) throw new SkipException("(Windows only)");
    }

    public static void Equal<TV>(TV expected, TV actual, string what = "")
    {
        if (!EqualityComparer<TV>.Default.Equals(expected, actual))
            throw new AssertException(what + "\n expected: " + Show(expected) + "\n actual:   " + Show(actual));
    }

    public static void True(bool condition, string what)
    {
        if (!condition) throw new AssertException(what);
    }

    public static void Contains(string expectedPart, string actual, string what = "")
    {
        if (actual == null || actual.IndexOf(expectedPart, StringComparison.Ordinal) < 0)
            throw new AssertException(what + "\n expected to contain: " + expectedPart + "\n actual: " + Show(actual));
    }

    public static void NotContains(string part, string actual, string what = "")
    {
        if (actual != null && actual.IndexOf(part, StringComparison.Ordinal) >= 0)
            throw new AssertException(what + "\n expected NOT to contain: " + part + "\n actual: " + Show(actual));
    }

    // Action must throw TE whose message contains `part`.
    public static void Throws<TE>(Action action, string part) where TE : Exception
    {
        try { action(); }
        catch (TE e)
        {
            Contains(part, e.Message, "exception message");
            return;
        }
        throw new AssertException("expected " + typeof(TE).Name + " containing: " + part);
    }

    static string Show(object o)
    {
        if (o == null) return "null";
        string s = o.ToString();
        return s.Length > 600 ? s.Substring(0, 600) + "... (" + s.Length + " chars)" : s;
    }

    // JSON with ' instead of " (verbatim C# strings stay readable).
    public static string J(string s) { return s.Replace('\'', '"'); }

    public static RelayConfig Config(string json) { return RelayConfig.Parse(J(json), "test.json"); }

    public static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "agent-relay-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }

    public static string TestsExe { get { return Assembly.GetExecutingAssembly().Location; } }

    public static string Utf8(byte[] b) { return Encoding.UTF8.GetString(b); }
    public static byte[] Bytes(string s) { return new UTF8Encoding(false).GetBytes(s); }
}

// Rules without opaque fields, for walker tests.
class PlainRules : AgentRules
{
    public override string Name { get { return "plain"; } }
    public override bool ShouldTranslate(string[] args) { return true; }
    public override bool IsOpaqueField(string name, string parentName) { return false; }
}
