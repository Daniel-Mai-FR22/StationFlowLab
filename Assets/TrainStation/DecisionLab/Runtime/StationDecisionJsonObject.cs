using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Strict JSON syntax with raw value fragments: no floating-point parsing, rounding or respelling.</summary>
    public sealed class StationDecisionJsonObject
    {
        readonly List<string> keys = new List<string>();
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        public IEnumerable<string> Keys => keys;
        public int Count => keys.Count;
        public string this[string key] { get => values[key]; set { if (!values.ContainsKey(key)) keys.Add(key); values[key] = value; } }
        public bool Has(string key) => values.ContainsKey(key);
        public bool TryGet(string key, out string value) => values.TryGetValue(key, out value);
        public bool Remove(string key) { if (!values.Remove(key)) return false; keys.Remove(key); return true; }
        public StationDecisionJsonObject Clone() { var result = new StationDecisionJsonObject(); foreach (string k in keys) result[k] = values[k]; return result; }
        public long Long(string key) { if (!long.TryParse(this[key], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result)) throw new FormatException("Expected JSON integer: " + key); return result; }
        public string Text(string key) => Unquote(this[key]);
        public override string ToString()
        { var b = new StringBuilder(); b.Append('{'); for (int i = 0; i < keys.Count; ++i) { if (i > 0) b.Append(','); AppendQuoted(b, keys[i]); b.Append(':').Append(values[keys[i]]); } return b.Append('}').ToString(); }
        public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        public static StationDecisionJsonObject Parse(string json) => ParseCore(json, false);
        /// <summary>Validate once and remove only insignificant whitespace; numeric and string spellings stay exact.</summary>
        internal static StationDecisionJsonObject ParseCompact(string json) => ParseCore(json, true);
        static StationDecisionJsonObject ParseCore(string json, bool compactValues)
        {
            if (json == null) throw new ArgumentNullException(nameof(json)); int i = 0; White(json, ref i);
            if (i >= json.Length || json[i++] != '{') throw new FormatException("Expected a JSON object.");
            var result = new StationDecisionJsonObject(); White(json, ref i);
            if (Take(json, ref i, '}')) { End(json, i); return result; }
            while (true)
            {
                White(json, ref i); int k = i; StringEnd(json, ref i); string key = UnquoteValidated(json, k, i - k);
                if (result.Has(key)) throw new FormatException("Duplicate JSON field: " + key);
                White(json, ref i); Expect(json, ref i, ':'); White(json, ref i); int start = i; ValueEnd(json, ref i, 1);
                result[key] = compactValues ? CompactValidated(json, start, i - start) : json.Substring(start, i - start); White(json, ref i);
                if (Take(json, ref i, '}')) break; Expect(json, ref i, ',');
            }
            End(json, i); return result;
        }
        public static List<string> Array(string json)
        {
            int i = 0; White(json, ref i); Expect(json, ref i, '['); White(json, ref i); var result = new List<string>();
            if (Take(json, ref i, ']')) { End(json, i); return result; }
            while (true) { White(json, ref i); int start = i; ValueEnd(json, ref i, 1); result.Add(json.Substring(start, i - start)); White(json, ref i); if (Take(json, ref i, ']')) break; Expect(json, ref i, ','); }
            End(json, i); return result;
        }
        public static string Compact(string json)
        {
            int pos = 0; White(json, ref pos); ValueEnd(json, ref pos, 0); End(json, pos);
            return CompactValidated(json, 0, json.Length);
        }
        // Called only for fragments already checked by ValueEnd. Avoid a builder entirely
        // when Unity's compact serializer has supplied no insignificant whitespace.
        static string CompactValidated(string json, int start, int length)
        {
            StringBuilder output = null; bool quoted = false, escape = false;
            for (int i = start, end = start + length; i < end; ++i)
            {
                char c = json[i];
                if (quoted) { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == '"') quoted = false; }
                else if (c == '"') quoted = true;
                else if (c == ' ' || c == '\r' || c == '\n' || c == '\t')
                {
                    if (output == null) { output = new StringBuilder(length); output.Append(json, start, i - start); }
                    continue;
                }
                output?.Append(c);
            }
            return output == null ? json.Substring(start, length) : output.ToString();
        }
        public static string Quote(string value)
        { var b = new StringBuilder(); AppendQuoted(b, value); return b.ToString(); }
        static void AppendQuoted(StringBuilder b, string value)
        {
            b.Append('"'); foreach (char c in value ?? "")
                switch (c) { case '"': b.Append("\\\""); break; case '\\': b.Append("\\\\"); break; case '\n': b.Append("\\n"); break; case '\r': b.Append("\\r"); break; case '\t': b.Append("\\t"); break; case '\b': b.Append("\\b"); break; case '\f': b.Append("\\f"); break; default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else b.Append(c); break; }
            b.Append('"');
        }
        public static string Unquote(string raw)
        {
            int end = 0; StringEnd(raw, ref end); if (end != raw.Length) throw new FormatException("Invalid JSON string suffix.");
            return UnquoteValidated(raw, 0, raw.Length);
        }
        static string UnquoteValidated(string raw, int start, int length)
        {
            int end = start + length - 1;
            if (raw.IndexOf('\\', start + 1, length - 2) < 0) return raw.Substring(start + 1, length - 2);
            var b = new StringBuilder(); for (int i = start + 1; i < end; ++i)
            { char c = raw[i]; if (c != '\\') { b.Append(c); continue; } c = raw[++i]; switch (c) { case 'u': b.Append((char)int.Parse(raw.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break; case 'b': b.Append('\b'); break; case 'f': b.Append('\f'); break; case 'n': b.Append('\n'); break; case 'r': b.Append('\r'); break; case 't': b.Append('\t'); break; default: b.Append(c); break; } }
            return b.ToString();
        }
        static void White(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\r' || s[i] == '\n' || s[i] == '\t')) ++i; }
        static void End(string s, int i) { White(s, ref i); if (i != s.Length) throw new FormatException("Trailing JSON content."); }
        static bool Take(string s, ref int i, char c) { if (i < s.Length && s[i] == c) { ++i; return true; } return false; }
        static void Expect(string s, ref int i, char c) { if (!Take(s, ref i, c)) throw new FormatException("Expected JSON token: " + c); }
        static void StringEnd(string s, ref int i)
        {
            Expect(s, ref i, '"'); while (i < s.Length)
            { char c = s[i++]; if (c == '"') return; if (c < 32) throw new FormatException("Unescaped control character in JSON string."); if (c != '\\') continue; if (i >= s.Length) break; c = s[i++]; if (c == 'u') { if (i + 4 > s.Length) break; for (int n = 0; n < 4; ++n) if (!Uri.IsHexDigit(s[i++])) throw new FormatException("Invalid Unicode JSON escape."); } else if ("\"\\/bfnrt".IndexOf(c) < 0) throw new FormatException("Invalid JSON escape."); }
            throw new FormatException("Unterminated JSON string.");
        }
        static void ValueEnd(string s, ref int i, int depth)
        {
            if (depth > 128 || i >= s.Length) throw new FormatException("JSON depth/end is invalid."); char c = s[i];
            if (c == '"') { StringEnd(s, ref i); return; }
            if (c == '{')
            {
                ++i; White(s, ref i); if (Take(s, ref i, '}')) return; var names = new HashSet<string>(StringComparer.Ordinal);
                while (true) { White(s, ref i); int k = i; StringEnd(s, ref i); if (!names.Add(UnquoteValidated(s, k, i - k))) throw new FormatException("Duplicate nested JSON field."); White(s, ref i); Expect(s, ref i, ':'); White(s, ref i); ValueEnd(s, ref i, depth + 1); White(s, ref i); if (Take(s, ref i, '}')) return; Expect(s, ref i, ','); }
            }
            if (c == '[') { ++i; White(s, ref i); if (Take(s, ref i, ']')) return; while (true) { White(s, ref i); ValueEnd(s, ref i, depth + 1); White(s, ref i); if (Take(s, ref i, ']')) return; Expect(s, ref i, ','); } }
            string literal = c == 't' ? "true" : c == 'f' ? "false" : c == 'n' ? "null" : null;
            if (literal != null && i + literal.Length <= s.Length && string.CompareOrdinal(s, i, literal, 0, literal.Length) == 0) { i += literal.Length; return; }
            int start = i; Take(s, ref i, '-'); if (!Take(s, ref i, '0')) { if (i >= s.Length || s[i] < '1' || s[i] > '9') throw new FormatException("Invalid JSON number."); while (i < s.Length && s[i] >= '0' && s[i] <= '9') ++i; }
            if (Take(s, ref i, '.')) { int digits = i; while (i < s.Length && s[i] >= '0' && s[i] <= '9') ++i; if (digits == i) throw new FormatException("Invalid JSON fraction."); }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E')) { ++i; if (i < s.Length && (s[i] == '-' || s[i] == '+')) ++i; int digits = i; while (i < s.Length && s[i] >= '0' && s[i] <= '9') ++i; if (digits == i) throw new FormatException("Invalid JSON exponent."); }
            if (start == i) throw new FormatException("Invalid JSON value.");
        }
    }
}
