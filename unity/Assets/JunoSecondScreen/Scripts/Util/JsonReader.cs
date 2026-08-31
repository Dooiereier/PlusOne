namespace JunoSecondScreen.Util
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// A minimal JSON parser for the small command objects the console sends.
    /// Objects become <see cref="Dictionary{TKey,TValue}"/>, arrays become
    /// <see cref="List{T}"/>, numbers become <see cref="double"/>.
    /// </summary>
    internal static class JsonReader
    {
        /// <summary>
        /// Parses a JSON document.
        /// </summary>
        /// <param name="text">The document text.</param>
        /// <returns>The parsed value, or <c>null</c> if the text is not valid JSON.</returns>
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            int index = 0;
            try
            {
                object value = ParseValue(text, ref index);
                SkipWhitespace(text, ref index);
                return index == text.Length ? value : null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        public static string GetString(Dictionary<string, object> map, string key, string fallback = null)
        {
            return map != null && map.TryGetValue(key, out var value) && value is string text ? text : fallback;
        }

        public static double GetDouble(Dictionary<string, object> map, string key, double fallback = 0d)
        {
            return map != null && map.TryGetValue(key, out var value) && value is double number ? number : fallback;
        }

        public static int GetInt(Dictionary<string, object> map, string key, int fallback = 0)
        {
            return (int)Math.Round(GetDouble(map, key, fallback));
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool fallback = false)
        {
            return map != null && map.TryGetValue(key, out var value) && value is bool flag ? flag : fallback;
        }

        private static object ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
            {
                throw new FormatException("Unexpected end of JSON.");
            }

            char c = text[index];
            switch (c)
            {
                case '{': return ParseObject(text, ref index);
                case '[': return ParseArray(text, ref index);
                case '"': return ParseString(text, ref index);
                case 't': Expect(text, ref index, "true"); return true;
                case 'f': Expect(text, ref index, "false"); return false;
                case 'n': Expect(text, ref index, "null"); return null;
                default: return ParseNumber(text, ref index);
            }
        }

        private static Dictionary<string, object> ParseObject(string text, ref int index)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            index++;
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}')
            {
                index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                string key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (text[index] != ':')
                {
                    throw new FormatException("Expected ':' in JSON object.");
                }

                index++;
                result[key] = ParseValue(text, ref index);
                SkipWhitespace(text, ref index);
                char c = text[index++];
                if (c == '}')
                {
                    return result;
                }

                if (c != ',')
                {
                    throw new FormatException("Expected ',' or '}' in JSON object.");
                }
            }
        }

        private static List<object> ParseArray(string text, ref int index)
        {
            var result = new List<object>();
            index++;
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']')
            {
                index++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(text, ref index));
                SkipWhitespace(text, ref index);
                char c = text[index++];
                if (c == ']')
                {
                    return result;
                }

                if (c != ',')
                {
                    throw new FormatException("Expected ',' or ']' in JSON array.");
                }
            }
        }

        private static string ParseString(string text, ref int index)
        {
            if (text[index] != '"')
            {
                throw new FormatException("Expected a JSON string.");
            }

            index++;
            var builder = new StringBuilder();
            while (true)
            {
                char c = text[index++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        builder.Append((char)int.Parse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        throw new FormatException("Unknown escape sequence in JSON string.");
                }
            }
        }

        private static double ParseNumber(string text, ref int index)
        {
            int start = index;
            while (index < text.Length && "+-.eE0123456789".IndexOf(text[index]) >= 0)
            {
                index++;
            }

            if (!double.TryParse(text.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new FormatException("Invalid JSON number.");
            }

            return value;
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("Invalid JSON literal.");
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }
    }
}
