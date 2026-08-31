namespace JunoSecondScreen.Net
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A parsed HTTP request line, headers and (optional) body.
    /// </summary>
    internal sealed class HttpRequest
    {
        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Method { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the request path with the query string removed, for example <c>/app.js</c>.
        /// </summary>
        public string Path { get; private set; } = "/";

        public string Body { get; set; } = string.Empty;

        public IReadOnlyDictionary<string, string> Headers => _headers;

        /// <summary>
        /// Builds a request from its start line.
        /// </summary>
        /// <param name="startLine">The raw request line, for example <c>GET /app.js?t=abc HTTP/1.1</c>.</param>
        /// <returns>The request, or <c>null</c> if the line is malformed.</returns>
        public static HttpRequest FromStartLine(string startLine)
        {
            if (string.IsNullOrEmpty(startLine))
            {
                return null;
            }

            string[] parts = startLine.Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            var request = new HttpRequest { Method = parts[0].ToUpperInvariant() };
            string target = parts[1];
            int queryStart = target.IndexOf('?');
            if (queryStart >= 0)
            {
                request.ParseQuery(target.Substring(queryStart + 1));
                target = target.Substring(0, queryStart);
            }

            request.Path = UrlDecode(target);
            return request;
        }

        public void AddHeaderLine(string line)
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                return;
            }

            _headers[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        public string GetHeader(string name)
        {
            return _headers.TryGetValue(name, out string value) ? value : null;
        }

        public string GetQuery(string name)
        {
            return _query.TryGetValue(name, out string value) ? value : null;
        }

        /// <summary>
        /// Gets a value indicating whether the client asked to upgrade to a WebSocket.
        /// </summary>
        public bool IsWebSocketUpgrade()
        {
            string upgrade = GetHeader("Upgrade");
            return upgrade != null
                && upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0
                && GetHeader("Sec-WebSocket-Key") != null;
        }

        private void ParseQuery(string query)
        {
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }

                int equals = pair.IndexOf('=');
                if (equals < 0)
                {
                    _query[UrlDecode(pair)] = string.Empty;
                }
                else
                {
                    _query[UrlDecode(pair.Substring(0, equals))] = UrlDecode(pair.Substring(equals + 1));
                }
            }
        }

        private static string UrlDecode(string value)
        {
            if (value.IndexOf('%') < 0 && value.IndexOf('+') < 0)
            {
                return value;
            }

            var builder = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '+')
                {
                    builder.Append(' ');
                }
                else if (c == '%' && i + 2 < value.Length
                    && byte.TryParse(value.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte decoded))
                {
                    builder.Append((char)decoded);
                    i += 2;
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }
}
