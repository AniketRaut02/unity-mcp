using System.Collections.Generic;

namespace UnityMCP.Setup
{
    /// <summary>
    /// A minimal, purpose-built JSON reader — NOT a general parser. It only answers one
    /// question: for a flat JSON object, what are its keys, and what is the raw
    /// (unparsed, still-serialized) text of each key's value? That's deliberately a much
    /// smaller and more verifiable claim than "correctly understands arbitrary JSON",
    /// and it's exactly what's needed to merge a new server entry into an existing
    /// mcpServers file without needing to fully understand entries this project didn't
    /// generate (which might be remote-URL servers, have extra fields, etc.) — they're
    /// preserved byte-for-byte as opaque text, only spliced around.
    ///
    /// Built instead of relying on the project's existing Newtonsoft.Json dependency
    /// because the stub environment's fake JsonConvert (SerializeObject always returns
    /// "{}", DeserializeObject always returns default) makes real merge behavior
    /// impossible to test there — and this narrow, self-contained implementation is
    /// fully testable on its own regardless.
    /// </summary>
    internal static class MCPMiniJson
    {
        /// <summary>
        /// Parses `json` as a flat object and returns each key mapped to the raw text of
        /// its value. Values that are themselves objects/arrays are NOT recursed into —
        /// their entire raw text (braces/brackets and all) is captured as one opaque
        /// string. Returns false with a message in `error` for anything that isn't a
        /// well-formed JSON object at the top level; never throws.
        /// </summary>
        public static bool TryExtractObjectEntries(string json, out Dictionary<string, string> entries, out string error)
        {
            entries = new Dictionary<string, string>();
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                return true; // nothing to parse -- an empty object, valid starting point
            }

            int pos = SkipWhitespace(json, 0);
            if (pos >= json.Length || json[pos] != '{')
            {
                error = "expected a JSON object at the top level";
                return false;
            }
            pos++; // consume '{'

            while (true)
            {
                pos = SkipWhitespace(json, pos);
                if (pos >= json.Length)
                {
                    error = "unexpected end of input inside an object";
                    return false;
                }
                if (json[pos] == '}')
                {
                    pos++;
                    break; // empty object, or end after trailing entries
                }

                if (json[pos] != '"')
                {
                    error = $"expected a quoted key at position {pos}";
                    return false;
                }
                int keyStart = pos;
                int keyEnd = SkipString(json, pos);
                if (keyEnd > json.Length || keyEnd <= keyStart + 1)
                {
                    error = $"malformed key string starting at position {pos}";
                    return false;
                }
                string key = json.Substring(keyStart + 1, keyEnd - keyStart - 2);
                pos = keyEnd;

                pos = SkipWhitespace(json, pos);
                if (pos >= json.Length || json[pos] != ':')
                {
                    error = $"expected ':' after key '{key}'";
                    return false;
                }
                pos++;

                pos = SkipWhitespace(json, pos);
                int valueStart = pos;
                int valueEnd = SkipValue(json, pos);
                if (valueEnd <= valueStart)
                {
                    error = $"could not parse a value for key '{key}'";
                    return false;
                }
                entries[key] = json.Substring(valueStart, valueEnd - valueStart);
                pos = valueEnd;

                pos = SkipWhitespace(json, pos);
                if (pos < json.Length && json[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (pos < json.Length && json[pos] == '}')
                {
                    pos++;
                    break;
                }

                error = $"expected ',' or '}}' after the value for key '{key}'";
                return false;
            }

            return true;
        }

        private static int SkipValue(string s, int pos)
        {
            pos = SkipWhitespace(s, pos);
            if (pos >= s.Length) return pos;

            char c = s[pos];
            if (c == '"')
            {
                return SkipString(s, pos);
            }

            if (c == '{' || c == '[')
            {
                char open = c;
                char close = c == '{' ? '}' : ']';
                int depth = 0;
                bool inString = false;
                for (int i = pos; i < s.Length; i++)
                {
                    char ch = s[i];
                    if (inString)
                    {
                        if (ch == '\\') { i++; continue; } // skip the escaped character too
                        if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"') { inString = true; continue; }
                    if (ch == open) depth++;
                    else if (ch == close)
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                }
                return s.Length; // unterminated -- caller's length check will flag this
            }

            // A primitive (number, true, false, null): scan until a structural delimiter.
            int j = pos;
            while (j < s.Length && s[j] != ',' && s[j] != '}' && s[j] != ']' && !char.IsWhiteSpace(s[j])) j++;
            return j;
        }

        private static int SkipString(string s, int pos)
        {
            // pos is expected to point at the opening quote.
            int i = pos + 1;
            while (i < s.Length)
            {
                if (s[i] == '\\') { i += 2; continue; }
                if (s[i] == '"') return i + 1;
                i++;
            }
            return s.Length; // unterminated
        }

        private static int SkipWhitespace(string s, int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            return pos;
        }
    }
}
