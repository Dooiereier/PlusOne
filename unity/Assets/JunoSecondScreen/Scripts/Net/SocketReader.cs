namespace JunoSecondScreen.Net
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Buffered reader over a network stream. Buffering is required because the
    /// request body (and the first WebSocket frame) can arrive in the same TCP
    /// segment as the headers.
    /// </summary>
    internal sealed class SocketReader
    {
        private const int MaxLineLength = 8192;

        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public SocketReader(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// Reads a CRLF terminated line.
        /// </summary>
        /// <returns>The line without its terminator, or <c>null</c> at end of stream.</returns>
        public string ReadLine()
        {
            var builder = new StringBuilder(128);
            while (true)
            {
                if (_start == _end && !Fill())
                {
                    return builder.Length > 0 ? builder.ToString() : null;
                }

                byte b = _buffer[_start++];
                if (b == '\n')
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] == '\r')
                    {
                        builder.Length--;
                    }

                    return builder.ToString();
                }

                if (builder.Length >= MaxLineLength)
                {
                    throw new IOException("HTTP header line too long.");
                }

                builder.Append((char)b);
            }
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes.
        /// </summary>
        public byte[] ReadExactly(int count)
        {
            var result = new byte[count];
            int filled = 0;
            while (filled < count)
            {
                if (_start == _end && !Fill())
                {
                    throw new IOException("Connection closed before the message was complete.");
                }

                int available = Math.Min(_end - _start, count - filled);
                Buffer.BlockCopy(_buffer, _start, result, filled, available);
                _start += available;
                filled += available;
            }

            return result;
        }

        private bool Fill()
        {
            _start = 0;
            _end = _stream.Read(_buffer, 0, _buffer.Length);
            return _end > 0;
        }
    }
}
