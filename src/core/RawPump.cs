using System;
using System.IO;
using System.Text;

// Pump destination: write + flush, handles a broken destination once (SPEC §9 steps 5–6).
public class PumpTarget
{
    static readonly byte[] Newline = { 10 };

    readonly Stream dst;
    readonly RelayLog log;
    readonly bool keepReading;
    public readonly string Direction;
    bool broken;

    // keepReading: on write failure keep draining the source (relay stdout) instead of stopping (child stdin).
    public PumpTarget(Stream dst, RelayLog log, string direction, bool keepReading)
    {
        this.dst = dst;
        this.log = log;
        this.keepReading = keepReading;
        Direction = direction;
    }

    // false → stop pumping.
    public bool Write(byte[] data, int count, bool addNewline)
    {
        if (broken) return keepReading;
        try
        {
            dst.Write(data, 0, count);
            if (addNewline) dst.Write(Newline, 0, 1);
            dst.Flush();
            return true;
        }
        catch (Exception e)
        {
            broken = true;
            log.Write(RelayLog.Basic, "ERROR", Direction + " write failed: " + e.Message);
            return keepReading;
        }
    }

    public void EndOfInput(bool closeDestination)
    {
        log.Write(RelayLog.Basic, "EOF", Direction);
        if (closeDestination) { try { dst.Close(); } catch (Exception) { } }
    }
}

// Raw byte pump: each read chunk is written and flushed at once (passthrough, stderr).
public static class RawPump
{
    // logLevel/entry: when to log each chunk and as what (e.g. Basic "AGENT STDERR", Full "... RECEIVED").
    public static void Run(Stream src, PumpTarget target, RelayLog log, int logLevel, string entry, bool closeAtEof)
    {
        var buf = new byte[8192];
        try
        {
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                if (log.IsOn(logLevel)) log.Write(logLevel, entry, Encoding.UTF8.GetString(buf, 0, n));
                if (!target.Write(buf, n, false)) return;
            }
        }
        catch (Exception e) { log.Write(RelayLog.Basic, "ERROR", target.Direction + " read failed: " + e.Message); }
        target.EndOfInput(closeAtEof);
    }
}
