using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Fedatather
{
    // Minimal JSON reader/writer: objects -> Dictionary<string, object>, arrays -> List<object>,
    // plus string, double, bool and null. Enough for DevTools-protocol replies and our own UI messages.
    static class Json
    {
        public static object Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = 0;
            return ReadValue(s, ref i);
        }

        public static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) && v is string ? (string)v : "";
        }

        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder(s == null ? 2 : s.Length + 2);
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[i];
            if (c == '{')
            {
                i++;
                Dictionary<string, object> obj = new Dictionary<string, object>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    SkipWs(s, ref i);
                    string key = ReadString(s, ref i);
                    SkipWs(s, ref i);
                    Expect(s, ref i, ':');
                    obj[key] = ReadValue(s, ref i);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                i++;
                List<object> arr = new List<object>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return arr; }
                while (true)
                {
                    arr.Add(ReadValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return arr;
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;

            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("Unexpected character in JSON at " + i + ".");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Word(string s, ref int i, string w)
        {
            if (i + w.Length > s.Length || string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("Expected '" + c + "' in JSON at " + i + ".");
            i++;
        }

        static string ReadString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int run = i;
                while (i < s.Length && s[i] != '"' && s[i] != '\\') i++;
                sb.Append(s, run, i - run);
                if (i >= s.Length) throw new FormatException("Unterminated JSON string.");
                if (s[i] == '"') { i++; return sb.ToString(); }

                i++; // backslash
                if (i >= s.Length) throw new FormatException("Unterminated JSON escape.");
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad JSON unicode escape.");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
        }
    }
}
