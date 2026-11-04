using System;

// Relay-own flags: only "--relay-*" and only as the first argument (SPEC §5).
public static class RelayArgs
{
    public const string Version = "--relay-version";
    public const string SelfTest = "--relay-self-test";
    public const string Help = "--relay-help";

    // Relay flag, or null when everything goes to the agent.
    public static string Flag(string[] args)
    {
        return args.Length > 0 && args[0].StartsWith("--relay-", StringComparison.Ordinal) ? args[0] : null;
    }

    public static string[] AgentArgs(string[] args)
    {
        if (Flag(args) == null) return args;
        var rest = new string[args.Length - 1];
        Array.Copy(args, 1, rest, 0, rest.Length);
        return rest;
    }

    public static string Usage(string agent)
    {
        return "agent-relay-" + agent + " " + RelayVersion.Value + "\n" +
               "Runs the " + agent + " agent in WSL, translating paths. Config: agent-relay.json beside the exe.\n" +
               "  --relay-version     print version\n" +
               "  --relay-self-test   check config and show the launch command (no agent start)\n" +
               "  --relay-help        this help\n" +
               "All other arguments go to the agent.";
    }
}
