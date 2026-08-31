namespace JunoSecondScreen.Net
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using JunoSecondScreen.Util;

    /// <summary>
    /// Handles one request on a connection.
    /// </summary>
    /// <param name="request">The parsed request.</param>
    /// <param name="connection">The connection the response must be written to.</param>
    internal delegate void RequestHandler(HttpRequest request, HttpConnection connection);

    /// <summary>
    /// A small HTTP/1.1 server built directly on <see cref="TcpListener"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpListener"/> is deliberately not used: on Windows it requires a
    /// URL ACL (or administrator rights) for any prefix other than localhost, which
    /// would stop the tablet on the LAN from ever reaching the game.
    /// </remarks>
    internal sealed class HttpServer
    {
        private const int MaxConcurrentConnections = 12;
        private const int IdleTimeoutMs = 60000;
        private const int MaxHeaderLines = 100;

        private readonly RequestHandler _handler;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _connectionCount;

        public HttpServer(RequestHandler handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public bool IsRunning => _running;

        public int Port { get; private set; }

        /// <summary>
        /// Binds the listener and starts accepting connections on a background thread.
        /// </summary>
        /// <param name="port">The TCP port to listen on.</param>
        /// <returns><c>true</c> if the server started.</returns>
        public bool Start(int port)
        {
            if (_running)
            {
                return true;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _running = true;

                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "SecondScreen HTTP",
                };
                _acceptThread.Start();
                return true;
            }
            catch (SocketException ex)
            {
                Log.Error($"Could not listen on port {port}: {ex.Message}. Pick a different port in the mod settings.");
                _listener = null;
                _running = false;
                return false;
            }
        }

        /// <summary>
        /// Stops the listener and drops every open connection.
        /// </summary>
        public void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // The listener is going away; nothing useful to do.
            }

            lock (_clients)
            {
                foreach (TcpClient client in _clients)
                {
                    try
                    {
                        client.Close();
                    }
                    catch (SocketException)
                    {
                    }
                }

                _clients.Clear();
            }

            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    // Thrown when the listener is stopped, and on transient accept errors.
                    if (_running)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    return;
                }

                if (Interlocked.Increment(ref _connectionCount) > MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref _connectionCount);
                    try
                    {
                        client.Close();
                    }
                    catch (SocketException)
                    {
                    }

                    continue;
                }

                lock (_clients)
                {
                    _clients.Add(client);
                }

                var thread = new Thread(() => ServeConnection(client))
                {
                    IsBackground = true,
                    Name = "SecondScreen Client",
                };
                thread.Start();
            }
        }

        private void ServeConnection(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = IdleTimeoutMs;
                client.SendTimeout = 15000;

                using (NetworkStream stream = client.GetStream())
                {
                    var reader = new SocketReader(stream);
                    while (_running && client.Connected)
                    {
                        HttpRequest request = ReadRequest(reader);
                        if (request == null)
                        {
                            return;
                        }

                        var connection = new HttpConnection(client, stream, reader);
                        _handler(request, connection);

                        if (connection.HasTakenOverConnection || !connection.KeepAlive)
                        {
                            return;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Client went away mid-request.
            }
            catch (SocketException)
            {
                // Client went away mid-request.
            }
            catch (ObjectDisposedException)
            {
                // Server stopped while this connection was being served.
            }
            catch (Exception ex)
            {
                Log.Warn($"Connection failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _connectionCount);
                lock (_clients)
                {
                    _clients.Remove(client);
                }

                try
                {
                    client.Close();
                }
                catch (SocketException)
                {
                }
            }
        }

        private static HttpRequest ReadRequest(SocketReader reader)
        {
            string startLine = reader.ReadLine();
            if (startLine == null)
            {
                return null;
            }

            HttpRequest request = HttpRequest.FromStartLine(startLine);
            if (request == null)
            {
                return null;
            }

            bool headersComplete = false;
            for (int i = 0; i < MaxHeaderLines; i++)
            {
                string line = reader.ReadLine();
                if (string.IsNullOrEmpty(line))
                {
                    headersComplete = true;
                    break;
                }

                request.AddHeaderLine(line);
            }

            if (!headersComplete)
            {
                // More headers than any real client sends; the rest of the stream can
                // no longer be framed reliably, so drop the connection.
                return null;
            }

            string lengthHeader = request.GetHeader("Content-Length");
            if (lengthHeader != null
                && int.TryParse(lengthHeader, out int length)
                && length > 0
                && length <= 64 * 1024)
            {
                request.Body = Encoding.UTF8.GetString(reader.ReadExactly(length));
            }

            return request;
        }
    }
}
