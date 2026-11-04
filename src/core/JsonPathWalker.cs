using System;
using System.Globalization;
using System.Text;

public class JsonWalkException : Exception
{
    public JsonWalkException(string message) : base(message) { }
}

// Token-preserving JSON walker (SPEC §8.2). Copies everything verbatim except strings and keys
// that the map function changes. Throws JsonWalkException if the line is not exactly one JSON value.
public class JsonPathWalker
{
    // field = member name of the value ("key in <member>" for keys).
    public delegate void ChangeHandler(string field, string oldValue, string newValue);

    const int MaxDepth = 512;

    readonly AgentRules rules;
    readonly Func<string, string> map;
    readonly ChangeHandler onChange;
    string s;
    int pos;
    StringBuilder sb;

    public JsonPathWalker(AgentRules rules, Func<string, string> map, ChangeHandler onChange)
    {
        this.rules = rules;
        this.map = map;
        this.onChange = onChange;
    }

    public string Translate(string line)
    {
        s = line;
        pos = 0;
        sb = new StringBuilder(line.Length + 16);
        Whitespace();
        if (pos >= s.Length) throw Error("empty line");
        Value("", "", false, 0);
        Whitespace();
        if (pos < s.Length) throw Error("trailing characters");
        return sb.ToString();
    }

    void Value(string member, string parent, bool opaque, int depth)
    {
        if (depth > MaxDepth) throw Error("nesting too deep");
        if (pos >= s.Length) throw Error("unexpected end");
        char c = s[pos];
        if (c == '{') Object(member, opaque, depth);
        else if (c == '[') Array(member, parent, opaque, depth);
        else if (c == '"') StringToken(member, opaque, false);
        else if (c == '-' || (c >= '0' && c <= '9')) Number();
        else if (!Literal("true") && !Literal("false") && !Literal("null")) throw Error("unexpected character");
    }

    void Object(string member, bool opaque, int depth)
    {
        sb.Append('{');
        pos++;
        Whitespace();
        if (Peek() == '}') { sb.Append('}'); pos++; return; }
        while (true)
        {
            Whitespace();
            if (Peek() != '"') throw Error("expected key");
            string key = StringToken(member, opaque, true);
            Whitespace();
            if (Peek() != ':') throw Error("expected ':'");
            sb.Append(':');
            pos++;
            Whitespace();
            bool childOpaque = opaque || rules.IsOpaqueField(key, member);
            Value(key, member, childOpaque, depth + 1);
            Whitespace();
            char c = Peek();
            sb.Append(c);
            pos++;
            if (c == '}') return;
            if (c != ',') throw Error("expected ',' or '}'");
        }
    }

    void Array(string member, string parent, bool opaque, int depth)
    {
        sb.Append('[');
        pos++;
        Whitespace();
        if (Peek() == ']') { sb.Append(']'); pos++; return; }
        while (true)
        {
            Whitespace();
            Value(member, parent, opaque, depth + 1);
            Whitespace();
            char c = Peek();
            sb.Append(c);
            pos++;
            if (c == ']') return;
            if (c != ',') throw Error("expected ',' or ']'");
        }
    }

    // Appends the string (original or translated) and returns its decoded (original) value.
    string StringToken(string member, bool opaque, bool isKey)
    {
        int start = pos;
        string value = ReadString();
        string mapped = opaque ? value : map(value);
        if (mapped == value)
        {
            sb.Append(s, start, pos - start);
        }
        else
        {
            sb.Append(Encode(mapped));
            if (onChange != null) onChange(isKey ? "key in " + member : member, value, mapped);
        }
        return value;
    }

    string ReadString()
    {
        pos++; // opening quote
        var v = new StringBuilder();
        while (true)
        {
            if (pos >= s.Length) throw Error("unterminated string");
            char c = s[pos++];
            if (c == '"') return v.ToString();
            if (c < 0x20) throw Error("control character in string");
            if (c != '\\') { v.Append(c); continue; }
            if (pos >= s.Length) throw Error("unterminated escape");
            char e = s[pos++];
            switch (e)
            {
                case '"': v.Append('"'); break;
                case '\\': v.Append('\\'); break;
                case '/': v.Append('/'); break;
                case 'b': v.Append('\b'); break;
                case 'f': v.Append('\f'); break;
                case 'n': v.Append('\n'); break;
                case 'r': v.Append('\r'); break;
                case 't': v.Append('\t'); break;
                case 'u':
                    int code;
                    if (pos + 4 > s.Length ||
                        !int.TryParse(s.Substring(pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                        throw Error("bad \\u escape");
                    v.Append((char)code);
                    pos += 4;
                    break;
                default: throw Error("bad escape");
            }
        }
    }

    void Number()
    {
        int start = pos;
        if (Peek() == '-') pos++;
        if (Peek() == '0') pos++;
        else if (!Digits()) throw Error("bad number");
        if (Peek() == '.') { pos++; if (!Digits()) throw Error("bad number"); }
        if (Peek() == 'e' || Peek() == 'E')
        {
            pos++;
            if (Peek() == '+' || Peek() == '-') pos++;
            if (!Digits()) throw Error("bad number");
        }
        sb.Append(s, start, pos - start);
    }

    bool Digits()
    {
        int start = pos;
        while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
        return pos > start;
    }

    bool Literal(string word)
    {
        if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0) return false;
        sb.Append(word);
        pos += word.Length;
        return true;
    }

    void Whitespace()
    {
        int start = pos;
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n')) pos++;
        sb.Append(s, start, pos - start);
    }

    char Peek() { return pos < s.Length ? s[pos] : '\0'; }

    JsonWalkException Error(string message)
    {
        return new JsonWalkException(message + " at " + pos);
    }

    // Minimal JSON string encoding: only '"', '\' and U+0000..U+001F are escaped.
    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
