using System;
using System.Collections.Generic;
using System.IO;

// T-P mapping, T-W walker, T-R rules, T-F past failures (translation part) (SPEC §12.2).
static class TranslationTests
{
    static PathMapper Mapper(params string[] pairs)
    {
        var list = new List<PathMapping>();
        for (int i = 0; i < pairs.Length; i += 2)
            list.Add(new PathMapping(PathMapper.NormalizeWindows(pairs[i]), PathMapper.NormalizeLinux(pairs[i + 1])));
        return new PathMapper(list);
    }

    static readonly PathMapper App = Mapper("C:\\app", "/work/app", "D:\\My Files", "/data");

    static string ToLinux(AgentRules rules, string json) { return Walk(rules, App, true, T.J(json)); }
    static string ToWindows(AgentRules rules, string json) { return Walk(rules, App, false, T.J(json)); }

    static string Walk(AgentRules rules, PathMapper m, bool toLinux, string line)
    {
        Func<string, string> map = toLinux ? (Func<string, string>)m.ToLinux : m.ToWindows;
        return new JsonPathWalker(rules, map, null).Translate(line);
    }

    // ---- T-P ----

    public static void MapBothDirections()
    {
        T.Equal("/work/app", App.ToLinux("C:\\app"));
        T.Equal("/work/app/src/a.cs", App.ToLinux("C:\\app\\src\\a.cs"));
        T.Equal("C:\\app", App.ToWindows("/work/app"));
        T.Equal("C:\\app\\src\\a.cs", App.ToWindows("/work/app/src/a.cs"));
        T.Equal("/data/x", App.ToLinux("D:\\My Files\\x"));
        T.Equal("D:\\My Files\\x", App.ToWindows("/data/x"));
    }

    public static void MapUnmappedUnchanged()
    {
        T.Equal("E:\\x", App.ToLinux("E:\\x"));
        T.Equal("/home/me", App.ToWindows("/home/me"));
        T.Equal("relative\\x", App.ToLinux("relative\\x"));
        T.Equal("work/app", App.ToWindows("work/app"));
        T.Equal("", App.ToLinux(""));
    }

    public static void MapLongestPrefix()
    {
        PathMapper m = Mapper("C:\\app", "/work/app", "C:\\app\\data", "/data", "C:", "/mnt/c");
        T.Equal("/data/x", m.ToLinux("C:\\app\\data\\x"));
        T.Equal("/work/app/datax", m.ToLinux("C:\\app\\datax"));
        T.Equal("/mnt/c/other", m.ToLinux("C:\\other"));
        T.Equal("C:\\app\\data\\x", m.ToWindows("/data/x"));
    }

    public static void MapDriveRoot()
    {
        PathMapper m = Mapper("C:\\", "/mnt/c");
        T.Equal("C:\\", m.ToWindows("/mnt/c"), "bare C: becomes C:\\");
        T.Equal("C:\\x", m.ToWindows("/mnt/c/x"));
        T.Equal("/mnt/c", m.ToLinux("C:"));
        T.Equal("/mnt/c/x", m.ToLinux("C:\\x"));
        T.Equal("/mnt/cat/file", m.ToWindows("/mnt/cat/file"), "/mnt/cat is not /mnt/c");
    }

    public static void MapBoundaryAndCase()
    {
        T.Equal("C:\\app-other", App.ToLinux("C:\\app-other"));
        T.Equal("/work/app-other", App.ToWindows("/work/app-other"));
        T.Equal("/work/app/X", App.ToLinux("c:\\APP\\X"), "Windows compare case-insensitive");
        T.Equal("/Work/App/x", App.ToWindows("/Work/App/x"), "Linux compare case-sensitive");
    }

    public static void MapSlashesAndNewlines()
    {
        T.Equal("/work/app/a/b", App.ToLinux("C:/app/a/b"));
        T.Equal("/work/app/a/b", App.ToLinux("C:\\app/a\\b"));
        T.Equal("C:\\app\nx", App.ToLinux("C:\\app\nx"), "multi-line untouched");
        T.Equal("/work/app\r\nx", App.ToWindows("/work/app\r\nx"));
    }

    public static void MapUnicode()
    {
        T.Equal("/data/café.txt", App.ToLinux("D:\\My Files\\café.txt"));
        T.Equal("D:\\My Files\\café.txt", App.ToWindows("/data/café.txt"));
    }

    // ---- T-W ----

    public static void WalkerByteIdentity()
    {
        string[] lines = {
            "{\"a\" : [1, -2.5e+3, 0.0, true,null ,false], \"b\":{}, \"c\":[] }",
            "  {\"x\":\"\\u00e9\\/\\n\\t\\\"\\\\\",\"y\":\"плюс\"}\r",
            "\"just a string\"",
            "42",
            "[\"/elsewhere\", \"E:\\\\x\"]",
        };
        foreach (string line in lines) T.Equal(line, Walk(new PlainRules(), App, true, line), "identity");
        foreach (string line in lines) T.Equal(line, Walk(new PlainRules(), App, false, line), "identity");
    }

    public static void WalkerBigNumber()
    {
        string line = "{\"id\":9007199254740993,\"cwd\":\"C:\\\\app\",\"f\":1.0000000000000000001}";
        string result = Walk(new PlainRules(), App, true, line);
        T.Equal("{\"id\":9007199254740993,\"cwd\":\"/work/app\",\"f\":1.0000000000000000001}", result);
    }

    public static void WalkerEscapesKeptWhenUntouched()
    {
        string line = "{\"p\":\"\\/work\\/other\",\"q\":\"\\u0041\"}";
        T.Equal(line, Walk(new PlainRules(), App, false, line));
    }

    public static void WalkerEscapedPathTranslated()
    {
        // "\/work\/app\/x" decodes to /work/app/x → translated and re-encoded minimally
        T.Equal("{\"p\":\"C:\\\\app\\\\x\"}", Walk(new PlainRules(), App, false, "{\"p\":\"\\/work\\/app\\/x\"}"));
    }

    public static void WalkerMinimalReencoding()
    {
        string line = "{\"p\":\"C:\\\\app\\\\café \\\"q\\\"\\t.txt\"}";
        T.Equal("{\"p\":\"/work/app/café \\\"q\\\"\\t.txt\"}", Walk(new PlainRules(), App, true, line));
        T.Equal("\"\\u0001\"", JsonPathWalker.Encode("\u0001"));
    }

    public static void WalkerInvalidInputThrows()
    {
        string[] bad = { "", "   ", "{", "{\"a\":1}x", "{\"a\":1} {}", "{'a':1}", "{\"a\":01}", "[1,]", "{\"a\" 1}",
                         "\"unterminated", "{\"a\":\"\\q\"}", "{\"a\":tru}", "{\"a\":\"\u0001\"}", "nul" };
        foreach (string line in bad)
            T.Throws<JsonWalkException>(() => Walk(new PlainRules(), App, true, line), "");
    }

    public static void WalkerReportsChanges()
    {
        var changes = new List<string>();
        var w = new JsonPathWalker(new PlainRules(), App.ToLinux, (f, o, n) => changes.Add(f + "|" + o + "|" + n));
        w.Translate(T.J(@"{'cwd':'C:\\app','projects':{'C:\\app\\x':{}}}"));
        T.Equal(2, changes.Count);
        T.Equal("cwd|C:\\app|/work/app", changes[0]);
        T.Equal("key in projects|C:\\app\\x|/work/app/x", changes[1]);
    }

    // ---- T-R ----

    public static void RulesGenericBehavior()
    {
        var r = new ClaudeRules();
        T.Equal(T.J(@"{'foo':'/work/app/x'}"), ToLinux(r, @"{'foo':'C:\\app\\x'}"), "unknown field with path translated");
        T.Equal(T.J(@"{'projects':{'/work/app':{'trust':'C:\\other'}}}"),
                ToLinux(r, @"{'projects':{'C:\\app':{'trust':'C:\\other'}}}"), "path key translated");
        T.Equal(T.J(@"{'foo':'see C:\\app\\x'}"), ToLinux(r, @"{'foo':'see C:\\app\\x'}"), "path later in string untouched");
        T.Equal(T.J(@"{'foo':'/work/app/f.txt is missing'}"), ToLinux(r, @"{'foo':'C:\\app\\f.txt is missing'}"),
                "string starting with path rewritten whole (known gap)");
        T.Equal(T.J(@"{'u':'file:///C:/app/x'}"), ToLinux(r, @"{'u':'file:///C:/app/x'}"), "URI untouched");
        T.Equal(T.J(@"{'u':'file:///work/app/x'}"), ToWindows(r, @"{'u':'file:///work/app/x'}"), "URI untouched");
    }

    public static void ClaudeMessageContent()
    {
        var r = new ClaudeRules();
        string line = @"{'type':'assistant','message':{'content':[
            {'type':'tool_use','input':{'file_path':'/work/app/a.txt','content':'/work/app','new_string':'/work/app','old_string':'/work/app'}},
            {'type':'text','text':'/work/app'},
            {'type':'tool_result','content':'/work/app/x'}]}}";
        string expected = line.Replace("'file_path':'/work/app/a.txt'", @"'file_path':'C:\\app\\a.txt'");
        T.Equal(T.J(expected), ToWindows(r, line));
        T.Equal(T.J(line), ToLinux(new ClaudeRules(), expected));
    }

    public static void ClaudeOpaque()
    {
        var r = new ClaudeRules();
        string[] same = {
            @"{'content':{'path':'/work/app'}}",
            @"{'tool_use_result':{'stdout':'/work/app','stderr':'/work/app'}}",
            @"{'input':{'command':'/work/app/run.sh'}}",
            @"{'delta':{'partial_json':'/work/app'}}",
            @"{'message':{'content':[{'type':'tool_result','content':[{'type':'text','text':'/work/app'}]}]}}",
        };
        foreach (string s in same) T.Equal(T.J(s), ToWindows(r, s));
        T.Equal(T.J(@"{'cwd':'C:\\app','transcript_path':'C:\\app\\t.jsonl','filePath':'C:\\app\\f'}"),
                ToWindows(r, @"{'cwd':'/work/app','transcript_path':'/work/app/t.jsonl','filePath':'/work/app/f'}"));
    }

    public static void ClaudeShouldTranslate()
    {
        var r = new ClaudeRules();
        T.True(r.ShouldTranslate(new[] { "--output-format", "stream-json", "--verbose" }), "stream-json");
        T.True(r.ShouldTranslate(new[] { "--input-format=stream-json" }), "contains stream-json");
        T.True(!r.ShouldTranslate(new[] { "auth", "status", "--json" }), "auth status --json passthrough");
        T.True(!r.ShouldTranslate(new string[0]), "no args");
    }

    public static void CodexRulesBehavior()
    {
        var r = new CodexRules();
        T.Equal(T.J(@"{'params':{'cwd':'/work/app','cwds':['/work/app','/data/q'],'projects':{'/work/app':{'trust_level':'trusted'}}}}"),
                ToLinux(r, @"{'params':{'cwd':'C:\\app','cwds':['C:\\app','D:\\My Files\\q'],'projects':{'C:\\app':{'trust_level':'trusted'}}}}"));
        string same = @"{'item':{'aggregatedOutput':'/work/app','delta':'/work/app','diff':'/work/app','message':'/work/app'}}";
        T.Equal(T.J(same), ToWindows(r, same));
        T.True(r.ShouldTranslate(new[] { "app-server" }), "app-server");
        T.True(!r.ShouldTranslate(new[] { "--version" }), "--version passthrough");
    }

    // ---- T-F (translation) ----

    // Vectors from the claude-wsl / codex-wsl POC self-tests.
    public static void PocVectors()
    {
        string windows = "C:\\Users\\bsw\\X\\DevLab\\funspintest", linux = "/work/funspintest";
        PathMapper m = Mapper(windows, linux, "D:\\My Files", "/data");
        T.Equal(linux + "/src", m.ToLinux(windows.ToUpperInvariant() + "\\src"));
        T.Equal(linux + "-other", m.ToWindows(linux + "-other"));
        T.Equal(linux, m.ToLinux(windows));
        T.Equal(windows, m.ToWindows(linux));
        T.Equal("/mnt/c", m.ToWindows("/mnt/c"));

        string w = JsonPathWalker.Encode(windows);
        string request = "{\"id\":9007199254740993,\"cwd\":" + w + ",\"projects\":{" + w +
                         ":{\"trust_level\":\"trusted\"}},\"text\":\"Keep C:\\\\Users unchanged\",\"paths\":[" + w + "]}";
        foreach (AgentRules rules in new AgentRules[] { new ClaudeRules(), new CodexRules() })
        {
            string translated = Walk(rules, m, true, request);
            T.Contains("9007199254740993", translated);
            T.Contains("\"cwd\":\"" + linux + "\"", translated);
            T.Contains("\"projects\":{\"" + linux + "\"", translated);
            T.Contains("\"text\":\"Keep C:\\\\Users unchanged\"", translated);
            T.Equal(request, Walk(rules, m, false, translated), "round trip");
        }
    }

    // Captured Codex value (D10): must not be made worse — one prefix replacement, nothing else.
    public static void RuntimeWorkspaceRootsVector()
    {
        PathMapper m = Mapper("C:\\Users\\bsw\\X\\DevLab\\funspintest", "/mnt/c/Users/bsw/X/DevLab/funspintest");
        string odd = "/mnt/c/Users/bsw/X/DevLab/funspintest/C:\\Users\\bsw\\X\\DevLab\\funspintest";
        T.Equal("C:\\Users\\bsw\\X\\DevLab\\funspintest\\C:\\Users\\bsw\\X\\DevLab\\funspintest", m.ToWindows(odd));
        string line = "{\"runtimeWorkspaceRoots\":[" + JsonPathWalker.Encode(odd) + "]}";
        T.Contains("C:\\\\Users\\\\bsw\\\\X\\\\DevLab\\\\funspintest\\\\C:", Walk(new CodexRules(), m, false, line));
    }

    // Line pump: original bytes for anything the walker rejects; never drops a line.
    public static void LinePumpForwardsOriginalOnError()
    {
        byte[] invalidUtf8 = { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D };  // {"a":"\xC3("}
        var input = new MemoryStream();
        input.Write(invalidUtf8, 0, invalidUtf8.Length);
        byte[] rest = T.Bytes("\nnot json\n\n{\"cwd\":\"C:\\\\app\"}\r\n{\"cwd\":\"C:\\\\app\"}");
        input.Write(rest, 0, rest.Length);
        input.Position = 0;
        var output = new MemoryStream();
        RelayLog log = RelayLog.None("t", TextWriter.Null);
        LinePump.Run(input, new PumpTarget(output, log, "X", false), new JsonPathWalker(new PlainRules(), App.ToLinux, null), log, false);

        var expected = new MemoryStream();
        expected.Write(invalidUtf8, 0, invalidUtf8.Length);
        byte[] tail = T.Bytes("\nnot json\n\n{\"cwd\":\"/work/app\"}\r\n{\"cwd\":\"C:\\\\app\"}");
        expected.Write(tail, 0, tail.Length);
        T.Equal(Convert.ToBase64String(expected.ToArray()), Convert.ToBase64String(output.ToArray()),
                "invalid UTF-8, non-JSON, empty kept; \\r kept; unterminated last fragment as-is");
    }
}
