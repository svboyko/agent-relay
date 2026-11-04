using System;
using System.Collections.Generic;

// Claude Code stream-json (SPEC §8.3).
public class ClaudeRules : AgentRules
{
    static readonly HashSet<string> Opaque = new HashSet<string> {
        "text", "old_string", "new_string", "command", "partial_json", "stdout", "stderr", "content" };

    public override string Name { get { return "claude"; } }

    public override bool ShouldTranslate(string[] args)
    {
        foreach (string a in args) if (a.Contains("stream-json")) return true;
        return false;
    }

    // message.content[] blocks are walked (tool_use.input.file_path); other "content" is opaque.
    public override bool IsOpaqueField(string name, string parentName)
    {
        if (name == "content" && parentName == "message") return false;
        return Opaque.Contains(name);
    }
}
