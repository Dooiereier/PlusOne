namespace JunoSecondScreen.Net
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    /// A minimal RFC 6455 server endpoint: enough of the protocol to exchange the
    /// console's JSON messages, handle fragmentation, and answer pings.
    /// </summary>
    internal sealed class WebSocketConnection
    {
        private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxMessageBytes = 256 * 1024;

        private readonly HttpConnection _connection;
        private readonly object _sendLock = new object();
        private bool _closed;

        private WebSocketConnection(HttpConnection connection)
        {
            _connection = connection;
        }

        public bool IsOpen => !_closed && _connection.IsConnected;

        /// <summary>
        /// Completes the opening handshake and takes over the connection.
        /// </summary>
        /// <param name="request">The upgrade request.</param>
        /// <param name="connection">The connection to upgrade.</param>
        /// <returns>The upgraded connection, or <c>null</c> if the handshake failed.</returns>
        public static WebSocketConnection Accept(HttpRequest request, HttpConnection connection)
        {
            string key = request.GetHeader("Sec-WebSocket-Key");
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            string accept;
            using (var sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
            }

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n" +
                "\r\n";

            byte[] bytes = Encoding.ASCII.GetBytes(response);
            connection.Stream.Write(bytes, 0, bytes.Length);
            connection.Stream.Flush();
            connection.TakeOverConnection();
            return new WebSocketConnection(connection);
        }

        /// <summary>
        /// Sends a text message. Safe to call from a different thread than the reader.
        /// </summary>
        /// <returns><c>true</c> if the message was written.</returns>
        public bool SendText(string message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            byte[] frame = BuildFrame(0x1, payload);

            lock (_sendLock)
            {
                if (_closed)
                {
                    return false;
                }

                try
                {
                    _connection.Stream.Write(frame, 0, frame.Length);
                    _connection.Stream.Flush();
                    return true;
                }
                catch (IOException)
                {
                    _closed = true;
                    return false;
                }
                catch (SocketException)
                {
                    _closed = true;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    _closed = true;
                    return false;
                }
            }
        }

        /// <summary>
        /// Blocks until a complete text message arrives.
        /// </summary>
        /// <returns>The message, or <c>null</c> when the connection closes.</returns>
        public string ReceiveText()
        {
            var message = new MemoryStream();
            int messageOpcode = 0;

            while (true)
            {
                Frame frame;
                try
                {
                    frame = ReadFrame();
                }
                catch (IOException)
                {
                    _closed = true;
                    return null;
                }
                catch (SocketException)
                {
                    _closed = true;
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    _closed = true;
                    return null;
                }

                switch (frame.Opcode)
                {
                    case 0x8: // close
                        SendControl(0x8, Array.Empty<byte>());
                        _closed = true;
                        return null;

                    case 0x9: // ping
                        SendControl(0xA, frame.Payload);
                        continue;

                    case 0xA: // pong
                        continue;

                    case 0x0: // continuation
                        if (messageOpcode == 0)
                        {
                            continue;
                        }

                        break;

                    default:
                        messageOpcode = frame.Opcode;
                        message.SetLength(0);
                        break;
                }

                if (message.Length + frame.Payload.Length > MaxMessageBytes)
                {
                    _closed = true;
                    return null;
                }

                message.Write(frame.Payload, 0, frame.Payload.Length);

                if (!frame.Final)
                {
                    continue;
                }

                string text = messageOpcode == 0x1 ? Encoding.UTF8.GetString(message.ToArray()) : string.Empty;
                message.SetLength(0);
                messageOpcode = 0;
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }

        public void Close()
        {
            lock (_sendLock)
            {
                _closed = true;
            }

            _connection.Close();
        }

        private void SendControl(int opcode, byte[] payload)
        {
            byte[] frame = BuildFrame(opcode, payload);
            lock (_sendLock)
            {
                if (_closed)
                {
                    return;
                }

                try
                {
                    _connection.Stream.Write(frame, 0, frame.Length);
                    _connection.Stream.Flush();
                }
                catch (IOException)
                {
                    _closed = true;
                }
                catch (SocketException)
                {
                    _closed = true;
                }
                catch (ObjectDisposedException)
                {
                    _closed = true;
                }
            }
        }

        private static byte[] BuildFrame(int opcode, byte[] payload)
        {
            int headerLength = payload.Length <= 125 ? 2 : payload.Length <= ushort.MaxValue ? 4 : 10;
            var frame = new byte[headerLength + payload.Length];
            frame[0] = (byte)(0x80 | opcode);

            if (payload.Length <= 125)
            {
                frame[1] = (byte)payload.Length;
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)payload.Length;
            }
            else
            {
                frame[1] = 127;
                long length = payload.Length;
                for (int i = 0; i < 8; i++)
                {
                    frame[2 + i] = (byte)(length >> ((7 - i) * 8));
                }
            }

            Buffer.BlockCopy(payload, 0, frame, headerLength, payload.Length);
            return frame;
        }

        private Frame ReadFrame()
        {
            byte[] header = _connection.Reader.ReadExactly(2);
            bool final = (header[0] & 0x80) != 0;
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;

            if (length == 126)
            {
                byte[] extended = _connection.Reader.ReadExactly(2);
                length = (extended[0] << 8) | extended[1];
            }
            else if (length == 127)
            {
                byte[] extended = _connection.Reader.ReadExactly(8);
                length = 0;
                for (int i = 0; i < 8; i++)
                {
                    length = (length << 8) | extended[i];
                }
            }

            if (length < 0 || length > MaxMessageBytes)
            {
                throw new IOException("WebSocket frame too large.");
            }

            byte[] mask = masked ? _connection.Reader.ReadExactly(4) : null;
            byte[] payload = length > 0 ? _connection.Reader.ReadExactly((int)length) : Array.Empty<byte>();

            if (mask != null)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i & 3];
                }
            }

            return new Frame(final, opcode, payload);
        }

        private readonly struct Frame
        {
            public Frame(bool final, int opcode, byte[] payload)
            {
                Final = final;
                Opcode = opcode;
                Payload = payload;
            }

            public bool Final { get; }

            public int Opcode { get; }

            public byte[] Payload { get; }
        }
    }
}
