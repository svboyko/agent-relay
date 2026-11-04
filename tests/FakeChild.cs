using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// Fake agent for lifecycle tests: agent-relay-tests.exe --fake-child <script> [arg]
static class FakeChild
{
    public static int Run(string[] args)
    {
        string script = args.Length > 1 ? args[1] : "";
        string arg = args.Length > 2 ? args[2] : null;
        Stream stdin = Console.OpenStandardInput(), stdout = Console.OpenStandardOutput(), stderr = Console.OpenStandardError();

        if (script.StartsWith("exit:")) return int.Parse(script.Substring(5));
        if (script.StartsWith("sleep:")) { Thread.Sleep(int.Parse(script.Substring(6))); return 0; }
        switch (script)
        {
            case "echo":  // stdin → stdout and stderr (stderr shows what the child really received)
                var buf = new byte[65536];
                int n;
                while ((n = stdin.Read(buf, 0, buf.Length)) > 0)
                {
                    stdout.Write(buf, 0, n); stdout.Flush();
                    stderr.Write(buf, 0, n); stderr.Flush();
                }
                return 0;
            case "ignore-eof":
                while (stdin.Read(new byte[4096], 0, 4096) > 0) { }
                Thread.Sleep(300);
                Write(stdout, "{\"cwd\":\"/work/app\",\"after\":\"eof\"}\n");
                return 0;
            case "stderr-bytes":
                stderr.Write(StderrBytes, 0, StderrBytes.Length);
                stderr.Flush();
                return 0;
            case "crash":  // simulated: a real unhandled exception may open a Windows Error Reporting dialog
                Write(stderr, "Unhandled Exception: System.InvalidOperationException: fake crash\n");
                return unchecked((int)0xE0434352);
            case "big-line":
                Write(stdout, "{\"text\":\"" + new string('a', 10 * 1024 * 1024) + "\",\"cwd\":\"/work/app\"}\n");
                return 0;
            case "unterminated":
                Write(stdout, "{\"cwd\":\"/work/app\"}\n{\"cwd\":\"/work/app\"}");
                return 0;
            case "prompt":  // prompt without newline, wait for an answer
                Write(stdout, "Password: ");
                int c;
                while ((c = stdin.ReadByte()) >= 0 && c != '\n') { }
                Write(stdout, "ok\n");
                return 0;
            case "grandchild":  // grandchild inherits stdout/stderr and keeps them open
                Process g = StartSelf("sleep:8000", false);
                File.WriteAllText(arg, g.Id.ToString());
                Write(stdout, "{\"cwd\":\"/work/app\"}\n");
                return 0;
            case "pid-sleep":
                File.WriteAllText(arg, Process.GetCurrentProcess().Id.ToString());
                Thread.Sleep(30000);
                return 0;
        }
        Write(stderr, "unknown fake-child script: " + script + "\n");
        return 99;
    }

    public static readonly byte[] StderrBytes = { 0xFF, 0xFE, (byte)'a', 13, 10, 0xC3, 0x28, (byte)'\\', 0 };

    static void Write(Stream s, string text)
    {
        byte[] b = new UTF8Encoding(false).GetBytes(text);
        s.Write(b, 0, b.Length);
        s.Flush();
    }

    // Starts this test exe with "--fake-child <script> [arg]" (via mono when needed).
    public static Process StartSelf(string script, bool redirect, string arg = null)
    {
        var psi = new ProcessStartInfo(SelfFileName, SelfArguments("--fake-child", script, arg));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = redirect;
        return Process.Start(psi);
    }

    public static string SelfFileName { get { return T.IsMono ? "mono" : T.TestsExe; } }

    public static string SelfArguments(string mode, params string[] rest)
    {
        string s = (T.IsMono ? ArgQuoter.Quote(T.TestsExe) + " " : "") + mode;
        foreach (string r in rest) if (r != null) s += " " + ArgQuoter.Quote(r);
        return s;
    }
}
