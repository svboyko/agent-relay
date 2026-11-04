using System;
using System.IO;
using System.Text;

// Line-framed translating pump (SPEC §7). Splits on 0x0A only; never drops a line.
public static class LinePump
{
    static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public static void Run(Stream src, PumpTarget target, JsonPathWalker walker, RelayLog log, bool closeAtEof)
    {
        var buf = new byte[65536];
        var pending = new MemoryStream();
        try
        {
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                int start = 0, nl;
                while ((nl = Array.IndexOf(buf, (byte)10, start, n - start)) >= 0)
                {
                    pending.Write(buf, start, nl - start);
                    if (!Forward(pending.ToArray(), target, walker, log)) return;
                    pending.SetLength(0);
                    start = nl + 1;
                }
                pending.Write(buf, start, n - start);
            }
            // Unterminated last fragment: forwarded as-is, no newline added.
            if (pending.Length > 0)
            {
                byte[] rest = pending.ToArray();
                if (log.IsOn(RelayLog.Full))
                    log.Write(RelayLog.Full, target.Direction + " RECEIVED", "(unterminated) " + Utf8.GetString(rest));
                target.Write(rest, rest.Length, false);
            }
        }
        catch (Exception e) { log.Write(RelayLog.Basic, "ERROR", target.Direction + " read failed: " + e.Message); }
        target.EndOfInput(closeAtEof);
    }

    static bool Forward(byte[] line, PumpTarget target, JsonPathWalker walker, RelayLog log)
    {
        byte[] output = line;
        string text = null;
        if (!IsBlank(line))
        {
            try
            {
                text = Strict.GetString(line);
                string translated = walker.Translate(text);
                if (translated != text) output = Utf8.GetBytes(translated);
            }
            catch (Exception e)
            {
                log.Write(RelayLog.Basic, "TRANSLATION ERROR",
                          target.Direction + ": " + e.Message + " (line length " + line.Length + ")");
            }
        }
        if (log.IsOn(RelayLog.Full))
        {
            log.Write(RelayLog.Full, target.Direction + " RECEIVED", text ?? Utf8.GetString(line));
            log.Write(RelayLog.Full, target.Direction + " FORWARDED",
                      output == line ? "(unchanged)" : Utf8.GetString(output));
        }
        return target.Write(output, output.Length, true);
    }

    static bool IsBlank(byte[] line)
    {
        foreach (byte b in line)
            if (b != 32 && b != 9 && b != 13) return false;
        return true;
    }
}
