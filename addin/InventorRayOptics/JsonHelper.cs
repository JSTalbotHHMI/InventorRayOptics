using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InventorRayOptics
{
    /// <summary>
    /// Minimal self-contained JSON reader/writer.
    ///
    /// Deliberately hand-rolled rather than using JavaScriptSerializer
    /// (System.Web.Extensions) or a NuGet package: Inventor 2025 hosts add-ins on modern
    /// .NET, whose AssemblyLoadContext has no GAC to probe and rejects .NET Framework
    /// reference assemblies outright — System.Web.Extensions failed to load at runtime
    /// there no matter how it was referenced or copied. Everything this add-in needs is
    /// small (parse a web message, re-emit an opaque payload), so owning it removes the
    /// dependency risk entirely.
    ///
    /// Deserialize produces: Dictionary&lt;string, object&gt; for objects,
    /// List&lt;object&gt; for arrays, string, double, bool, and null. Serialize accepts
    /// those same types plus any IDictionary/IEnumerable and the usual numeric types.
    /// </summary>
    internal static class JsonHelper
    {
        // ------------------------------------------------------------------ writing

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string str) { WriteString(sb, str); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }

            // "R" keeps full round-trip precision; JSON has no NaN/Infinity literals, so
            // those degrade to null rather than emitting something no parser accepts.
            if (value is double d)
            {
                sb.Append(double.IsNaN(d) || double.IsInfinity(d)
                    ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float f)
            {
                sb.Append(float.IsNaN(f) || float.IsInfinity(f)
                    ? "null" : f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is decimal || value is int || value is long || value is short ||
                value is byte || value is sbyte || value is uint || value is ulong || value is ushort)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }

            if (value is IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
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
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------ reading

        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            var value = ParseValue(json, ref i);
            SkipWhitespace(json, ref i);
            if (i != json.Length) throw new FormatException("Trailing characters after JSON value.");
            return value;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException($"Expected '{literal}' at position {i}.");
            i += literal.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException($"Expected object key at position {i}.");
                var key = ParseString(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException($"Expected ':' at position {i}.");
                i++;

                result[key] = ParseValue(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException($"Expected ',' or '}}' at position {i}.");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException($"Expected ',' or ']' at position {i}.");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string.");
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) throw new FormatException("Unterminated escape sequence.");
                var esc = s[i++];
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
                        if (i + 4 > s.Length) throw new FormatException("Truncated \\u escape.");
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"Invalid escape '\\{esc}' at position {i - 1}.");
                }
            }
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                    ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
            {
                i++;
            }
            var text = s.Substring(start, i - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Invalid number '{text}' at position {start}.");
            return value;
        }
    }
}
