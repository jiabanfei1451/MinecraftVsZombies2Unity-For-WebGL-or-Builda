// Builda SDK 内置极简 JSON（序列化/反序列化任意形态，JsonUtility 做不到）。
// 反序列化产物：object = Dictionary<string, object> | List<object> | string | double | bool | null。
// 仅供 Builda SDK 内部过桥使用；游戏业务数据请用自己的序列化方案。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Builda
{
    public static class BuildaJson
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            int pos = 0;
            return ParseValue(json, ref pos);
        }

        static void WriteValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case IDictionary dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        WriteValue(sb, e.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    break;
                }
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    // 其余整型/decimal 统一走 InvariantCulture 文本
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        static object ParseValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length)
            {
                throw new FormatException("BuildaJson: unexpected end of input");
            }
            char c = json[pos];
            switch (c)
            {
                case '{': return ParseObject(json, ref pos);
                case '[': return ParseArray(json, ref pos);
                case '"': return ParseString(json, ref pos);
                case 't': Expect(json, ref pos, "true"); return true;
                case 'f': Expect(json, ref pos, "false"); return false;
                case 'n': Expect(json, ref pos, "null"); return null;
                default: return ParseNumber(json, ref pos);
            }
        }

        static Dictionary<string, object> ParseObject(string json, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // '{'
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == '}')
            {
                pos++;
                return dict;
            }
            while (true)
            {
                SkipWhitespace(json, ref pos);
                string key = ParseString(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':')
                {
                    throw new FormatException("BuildaJson: expected ':' at " + pos);
                }
                pos++;
                dict[key] = ParseValue(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length)
                {
                    throw new FormatException("BuildaJson: unterminated object");
                }
                if (json[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (json[pos] == '}')
                {
                    pos++;
                    return dict;
                }
                throw new FormatException("BuildaJson: expected ',' or '}' at " + pos);
            }
        }

        static List<object> ParseArray(string json, ref int pos)
        {
            var list = new List<object>();
            pos++; // '['
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ']')
            {
                pos++;
                return list;
            }
            while (true)
            {
                list.Add(ParseValue(json, ref pos));
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length)
                {
                    throw new FormatException("BuildaJson: unterminated array");
                }
                if (json[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (json[pos] == ']')
                {
                    pos++;
                    return list;
                }
                throw new FormatException("BuildaJson: expected ',' or ']' at " + pos);
            }
        }

        static string ParseString(string json, ref int pos)
        {
            if (json[pos] != '"')
            {
                throw new FormatException("BuildaJson: expected string at " + pos);
            }
            pos++;
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (pos >= json.Length)
                {
                    break;
                }
                char esc = json[pos++];
                switch (esc)
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
                        if (pos + 4 > json.Length)
                        {
                            throw new FormatException("BuildaJson: bad \\u escape");
                        }
                        sb.Append((char)Convert.ToInt32(json.Substring(pos, 4), 16));
                        pos += 4;
                        break;
                    default:
                        throw new FormatException("BuildaJson: bad escape '\\" + esc + "'");
                }
            }
            throw new FormatException("BuildaJson: unterminated string");
        }

        static object ParseNumber(string json, ref int pos)
        {
            int start = pos;
            while (pos < json.Length && "-+.eE0123456789".IndexOf(json[pos]) >= 0)
            {
                pos++;
            }
            string text = json.Substring(start, pos - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new FormatException("BuildaJson: bad number '" + text + "'");
            }
            return value;
        }

        static void Expect(string json, ref int pos, string literal)
        {
            if (pos + literal.Length > json.Length || json.Substring(pos, literal.Length) != literal)
            {
                throw new FormatException("BuildaJson: expected '" + literal + "' at " + pos);
            }
            pos += literal.Length;
        }

        static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos]))
            {
                pos++;
            }
        }
    }
}
