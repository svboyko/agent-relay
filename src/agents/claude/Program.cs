static class Program
{
    static int Main(string[] args)
    {
        return RelayMain.Run("claude", new ClaudeRules(), args);
    }
}
