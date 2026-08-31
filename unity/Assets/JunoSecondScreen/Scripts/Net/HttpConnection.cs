namespace JunoSecondScreen.Net
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;

    /// <summary>
    /// The server side of one client connection: writes responses, and hands the raw
    /// stream over to the WebSocket and MJPEG endpoints when they take it over.
    /// </summary>
    internal sealed class HttpConnection
    {
        private static readonly byte[] MjpegBoundary = Encoding.ASCII.GetBytes("\r\n--frame\r\n");

        private readonly TcpClient _client;

        public HttpConnection(TcpClient client, Stream stream, SocketReader reader)
        {
            _client = client;
            Stream = stream;
            Reader = reader;
        }

        public Stream Stream { get; }

        public SocketReader Reader { get; }

        /// <summary>
        /// Gets a value indicating whether the connection can serve another request.
        /// </summary>
        public bool KeepAlive { get; private set; } = true;

        /// <summary>
        /// Gets a value indicating whether an endpoint has taken ownership of the raw
        /// stream (WebSocket or MJPEG), meaning the server loop must not touch it.
        /// </summary>
        public bool HasTakenOverConnection { get; private set; }

        public bool IsConnected => _client.Connected;

        /// <summary>
        /// Sends a complete response.
        /// </summary>
        public void Respond(int status, string contentType, byte[] body, IEnumerable<KeyValuePair<string, string>> extraHeaders = null)
        {
            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append((body?.Length ?? 0).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: keep-alive\r\n");
            if (extraHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in extraHeaders)
                {
                    head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            head.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            Stream.Write(headBytes, 0, headBytes.Length);
            if (body != null && body.Length > 0)
            {
                Stream.Write(body, 0, body.Length);
            }

            Stream.Flush();
        }

        public void RespondText(int status, string contentType, string body)
        {
            Respond(status, contentType, Encoding.UTF8.GetBytes(body));
        }

        /// <summary>
        /// Starts an MJPEG response and takes over the connection.
        /// </summary>
        public void BeginMjpeg()
        {
            HasTakenOverConnection = true;
            KeepAlive = false;
            const string Head =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n" +
                "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                "Pragma: no-cache\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(Head);
            Stream.Write(headBytes, 0, headBytes.Length);
            Stream.Flush();
        }

        /// <summary>
        /// Writes one JPEG frame into an MJPEG response started by <see cref="BeginMjpeg"/>.
        /// </summary>
        public void WriteMjpegFrame(byte[] jpeg)
        {
            Stream.Write(MjpegBoundary, 0, MjpegBoundary.Length);
            byte[] partHeader = Encoding.ASCII.GetBytes(
                "Content-Type: image/jpeg\r\nContent-Length: " + jpeg.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            Stream.Write(partHeader, 0, partHeader.Length);
            Stream.Write(jpeg, 0, jpeg.Length);
            Stream.Flush();
        }

        /// <summary>
        /// Marks the connection as owned by a long lived protocol such as WebSocket.
        /// </summary>
        public void TakeOverConnection()
        {
            HasTakenOverConnection = true;
            KeepAlive = false;
        }

        public void Close()
        {
            try
            {
                _client.Close();
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 503: return "Service Unavailable";
                default: return "OK";
            }
        }
    }
}
