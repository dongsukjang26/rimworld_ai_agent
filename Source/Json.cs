using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AIAdvisor
{
    /// <summary>
    /// 게임에 JSON 라이브러리가 없어서 쓰는 최소 JSON 파서/직렬화기.
    /// 객체는 Dictionary&lt;string, object&gt;, 배열은 List&lt;object&gt;, 숫자는 double 로 다룬다.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ParseValue();
            p.SkipWs();
            if (!p.End) throw new FormatException("Trailing characters in JSON at " + p.Pos);
            return v;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: sb.Append(FormatNumber(d)); return;
                case float f: sb.Append(FormatNumber(f)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    bool f2 = true;
                    foreach (var item in list)
                    {
                        if (!f2) sb.Append(',');
                        f2 = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    return;
                default:
                    WriteString(sb, v.ToString());
                    return;
            }
        }

        static string FormatNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (Math.Abs(d % 1) < 1e-9 && Math.Abs(d) < 1e15) return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            return Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
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
            sb.Append('"');
        }

        class Parser
        {
            readonly string s;
            int i;
            public Parser(string s) { this.s = s ?? ""; }
            public bool End => i >= s.Length;
            public int Pos => i;

            public void SkipWs()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ParseValue()
            {
                if (End) throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("Invalid JSON token at " + i);
                i += word.Length;
            }

            Dictionary<string, object> ParseObject()
            {
                var d = new Dictionary<string, object>();
                i++;
                SkipWs();
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();
                    if (s[i] != ':') throw new FormatException("Expected ':' at " + i);
                    i++;
                    SkipWs();
                    d[key] = ParseValue();
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + i);
                }
            }

            List<object> ParseArray()
            {
                var list = new List<object>();
                i++;
                SkipWs();
                if (s[i] == ']') { i++; return list; }
                while (true)
                {
                    SkipWs();
                    list.Add(ParseValue());
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException("Expected ',' or ']' at " + i);
                }
            }

            string ParseString()
            {
                if (s[i] != '"') throw new FormatException("Expected string at " + i);
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: throw new FormatException("Bad escape at " + i);
                    }
                }
            }

            double ParseNumber()
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (start == i) throw new FormatException("Unexpected character '" + s[i] + "' at " + i);
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>파싱된 JSON 트리를 편하게 읽기 위한 확장 메서드.</summary>
    public static class JsonExt
    {
        public static object Get(this object node, string key)
        {
            return node is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;
        }

        public static string Str(this object node, string key)
        {
            var v = node.Get(key);
            return v is string s ? s : v?.ToString();
        }

        public static double Num(this object node, string key, double fallback = 0)
        {
            var v = node.Get(key);
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            return fallback;
        }

        public static List<object> Arr(this object node, string key)
        {
            return node.Get(key) as List<object> ?? new List<object>();
        }
    }
}
