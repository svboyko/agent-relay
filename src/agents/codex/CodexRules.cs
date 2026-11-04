using System;
using System.Collections.Generic;

// Codex app-server JSON-RPC (SPEC §8.4). Opaque list to be reviewed against captured traffic.
public class CodexRules : AgentRules
{
    static readonly HashSet<string> Opaque = new HashSet<string> {
        "aggregatedOutput", "text", "delta", "command", "diff", "unifiedDiff", "content", "output",
        "stdout", "stderr", "summary", "message", "instructions", "baseInstructions", "developerInstructions" };

    public override string Name { get { return "codex"; } }

    public override bool ShouldTranslate(string[] args)
    {
        return Array.IndexOf(args, "app-server") >= 0;
    }

    public override bool IsOpaqueField(string name, string parentName)
    {
        return Opaque.Contains(name);
    }
}
