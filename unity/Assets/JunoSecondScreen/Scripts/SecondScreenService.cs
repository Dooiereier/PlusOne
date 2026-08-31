namespace JunoSecondScreen
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using JunoSecondScreen.Flight;
    using JunoSecondScreen.Net;
    using JunoSecondScreen.Util;
    using JunoSecondScreen.Web;
    using ModApi.Common;
    using UnityEngine;

    /// <summary>
    /// The mod's runtime: owns the HTTP server, publishes telemetry frames to
    /// connected tablets and feeds their commands back into the flight scene.
    /// </summary>
    internal sealed class SecondScreenService : MonoBehaviour
    {
        private const string TokenFileName = "SecondScreenToken.txt";
        private const int TelemetryWaitMs = 1000;

        private readonly TelemetryCollector _collector = new TelemetryCollector();
        private readonly CommandProcessor _commands = new CommandProcessor();
        private readonly object _telemetrySignal = new object();

        private HttpServer _server;
        private ViewCapture _viewCapture;
        private TelemetryFrame _frame;
        private ModConfiguration _configuration;
        private string _token = string.Empty;
        private string _persistentDataPath;
        private float _nextTelemetryTime;
        private float _nextConfigurationCheck;
        private int _consoleClients;
        private bool _configured;
        private bool _flightMessageShown;

        /// <summary>
        /// Gets the number of connected consoles.
        /// </summary>
        private int ConsoleClients => Volatile.Read(ref _consoleClients);

        private void Awake()
        {
            _persistentDataPath = Application.persistentDataPath;
            _token = LoadOrCreateToken();

            // The settings category may not be registered yet, so the first read
            // happens on the regular check below rather than here.
            _nextConfigurationCheck = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextConfigurationCheck)
            {
                _nextConfigurationCheck = Time.unscaledTime + 1f;
                if (ModConfiguration.TryRead(out ModConfiguration current) && !current.Equals(_configuration))
                {
                    if (_configured)
                    {
                        Log.Info("Settings changed, restarting the second screen server.");
                    }

                    _configured = true;
                    ApplyConfiguration(current);
                }
            }

            if (_server == null || !_server.IsRunning)
            {
                return;
            }

            _commands.Apply();
            PublishTelemetry();
            AnnounceInFlight();
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        /* ------------------------------------------------------------- lifecycle */

        private void ApplyConfiguration(ModConfiguration configuration)
        {
            Shutdown();
            _configuration = configuration;
            _commands.ControlEnabled = configuration.AllowControl;

            if (!configuration.Enabled)
            {
                Log.Info("Second screen is switched off in the mod settings.");
                return;
            }

            _server = new HttpServer(HandleRequest);
            if (!_server.Start(configuration.Port))
            {
                _server = null;
                return;
            }

            if (configuration.VideoEnabled)
            {
                _viewCapture = new ViewCapture(configuration.VideoWidth, configuration.VideoFps, configuration.VideoQuality);
                StartCoroutine(_viewCapture.CaptureLoop());
            }

            foreach (string line in BuildConnectionInfo())
            {
                Log.Info(line);
            }
        }

        private void Shutdown()
        {
            StopAllCoroutines();

            _server?.Stop();
            _server = null;

            _viewCapture?.Dispose();
            _viewCapture = null;

            _commands.Reset();
            _frame = null;
            _flightMessageShown = false;

            lock (_telemetrySignal)
            {
                Monitor.PulseAll(_telemetrySignal);
            }
        }

        /* -------------------------------------------------------------- telemetry */

        private void PublishTelemetry()
        {
            if (ConsoleClients == 0 || Time.unscaledTime < _nextTelemetryTime)
            {
                return;
            }

            _nextTelemetryTime = Time.unscaledTime + 1f / _configuration.TelemetryHz;

            string json;
            try
            {
                json = _collector.Build();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read telemetry: {ex.Message}");
                return;
            }

            lock (_telemetrySignal)
            {
                _frame = new TelemetryFrame(json, (_frame?.Version ?? 0) + 1);
                Monitor.PulseAll(_telemetrySignal);
            }
        }

        private TelemetryFrame WaitForTelemetry(int lastVersion)
        {
            lock (_telemetrySignal)
            {
                if (_frame == null || _frame.Version == lastVersion)
                {
                    Monitor.Wait(_telemetrySignal, TelemetryWaitMs);
                }

                return _frame != null && _frame.Version != lastVersion ? _frame : null;
            }
        }

        /// <summary>
        /// Shows the console address in the flight scene the first time a flight starts
        /// without a tablet attached, so the player does not have to dig through the log.
        /// </summary>
        private void AnnounceInFlight()
        {
            var flightSceneUi = Game.Instance.FlightScene?.FlightSceneUI;
            if (flightSceneUi == null)
            {
                _flightMessageShown = false;
                return;
            }

            if (_flightMessageShown || ConsoleClients > 0)
            {
                return;
            }

            _flightMessageShown = true;
            List<string> info = BuildConnectionInfo();
            if (info.Count > 0)
            {
                try
                {
                    flightSceneUi.ShowMessage(info[0], false, 8f);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not show the connection message: {ex.Message}");
                }
            }
        }

        private List<string> BuildConnectionInfo()
        {
            var lines = new List<string>();
            if (_server == null || !_server.IsRunning)
            {
                return lines;
            }

            string query = _configuration.RequireToken ? "/?t=" + _token : "/";
            foreach (string address in NetworkUtil.GetLocalAddresses())
            {
                lines.Add($"Second screen: http://{address}:{_server.Port}{query}");
            }

            if (lines.Count == 0)
            {
                lines.Add($"Second screen listening on port {_server.Port}, but no network address was found.");
            }

            return lines;
        }

        /* ----------------------------------------------------------------- routing */

        private void HandleRequest(HttpRequest request, HttpConnection connection)
        {
            if (!IsAuthorized(request))
            {
                connection.RespondText(401, "text/html; charset=utf-8", UnauthorizedPage);
                return;
            }

            switch (request.Path)
            {
                case "/ws":
                    ServeWebSocket(request, connection);
                    return;

                case "/stream.mjpg":
                    ServeVideo(connection);
                    return;

                case "/api/status":
                    connection.RespondText(200, "application/json; charset=utf-8", BuildStatusJson());
                    return;

                case "/api/command":
                    if (request.Method != "POST")
                    {
                        connection.RespondText(405, "text/plain; charset=utf-8", "POST only");
                        return;
                    }

                    _commands.Enqueue(request.Body);
                    connection.Respond(204, "text/plain; charset=utf-8", Array.Empty<byte>());
                    return;
            }

            if (WebAssets.TryGet(request.Path, out byte[] content, out string contentType))
            {
                connection.Respond(200, contentType, content, CookieHeaders(request));
                return;
            }

            connection.RespondText(404, "text/plain; charset=utf-8", "Not found");
        }

        private void ServeWebSocket(HttpRequest request, HttpConnection connection)
        {
            if (!request.IsWebSocketUpgrade())
            {
                connection.RespondText(400, "text/plain; charset=utf-8", "Expected a WebSocket upgrade");
                return;
            }

            WebSocketConnection socket = WebSocketConnection.Accept(request, connection);
            if (socket == null)
            {
                return;
            }

            Interlocked.Increment(ref _consoleClients);
            socket.SendText(BuildHelloJson());

            var pushThread = new Thread(() => PushTelemetry(socket))
            {
                IsBackground = true,
                Name = "SecondScreen Telemetry",
            };
            pushThread.Start();

            try
            {
                while (true)
                {
                    string message = socket.ReceiveText();
                    if (message == null)
                    {
                        break;
                    }

                    _commands.Enqueue(message);
                }
            }
            finally
            {
                socket.Close();
                pushThread.Join(500);
                if (Interlocked.Decrement(ref _consoleClients) == 0)
                {
                    _commands.Reset();
                }
            }
        }

        private void PushTelemetry(WebSocketConnection socket)
        {
            int lastVersion = 0;
            while (socket.IsOpen && _server != null && _server.IsRunning)
            {
                TelemetryFrame frame = WaitForTelemetry(lastVersion);
                if (frame == null)
                {
                    continue;
                }

                if (!socket.SendText(frame.Json))
                {
                    return;
                }

                lastVersion = frame.Version;
            }
        }

        private void ServeVideo(HttpConnection connection)
        {
            ViewCapture capture = _viewCapture;
            if (capture == null)
            {
                connection.RespondText(503, "text/plain; charset=utf-8", "The video feed is disabled in the mod settings.");
                return;
            }

            capture.AddSubscriber();
            try
            {
                connection.BeginMjpeg();
                int version = 0;
                while (connection.IsConnected && _server != null && _server.IsRunning)
                {
                    if (capture.WaitForFrame(version, 2000, out byte[] jpeg, out int newVersion))
                    {
                        version = newVersion;
                        connection.WriteMjpegFrame(jpeg);
                    }
                }
            }
            catch (IOException)
            {
                // The tablet closed the feed.
            }
            catch (System.Net.Sockets.SocketException)
            {
                // The tablet closed the feed.
            }
            finally
            {
                capture.RemoveSubscriber();
                connection.Close();
            }
        }

        /* -------------------------------------------------------------------- auth */

        private bool IsAuthorized(HttpRequest request)
        {
            if (!_configuration.RequireToken)
            {
                return true;
            }

            string supplied = request.GetQuery("t");
            if (supplied == null)
            {
                supplied = ReadTokenCookie(request.GetHeader("Cookie"));
            }

            return string.Equals(supplied, _token, StringComparison.Ordinal);
        }

        private IEnumerable<KeyValuePair<string, string>> CookieHeaders(HttpRequest request)
        {
            // Remember the token so the page's own asset and stream requests, which
            // carry no query string, stay authorized.
            if (!_configuration.RequireToken || request.GetQuery("t") == null)
            {
                return null;
            }

            return new[]
            {
                new KeyValuePair<string, string>(
                    "Set-Cookie",
                    $"jss={_token}; Path=/; Max-Age=31536000; SameSite=Lax"),
            };
        }

        private static string ReadTokenCookie(string cookieHeader)
        {
            if (string.IsNullOrEmpty(cookieHeader))
            {
                return null;
            }

            foreach (string cookie in cookieHeader.Split(';'))
            {
                string trimmed = cookie.Trim();
                if (trimmed.StartsWith("jss=", StringComparison.Ordinal))
                {
                    return trimmed.Substring(4);
                }
            }

            return null;
        }

        private string LoadOrCreateToken()
        {
            string path = Path.Combine(_persistentDataPath, TokenFileName);
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (existing.Length >= 6)
                    {
                        return existing;
                    }
                }
            }
            catch (IOException ex)
            {
                Log.Warn($"Could not read the saved access token: {ex.Message}");
            }

            string token = GenerateToken();
            try
            {
                File.WriteAllText(path, token);
            }
            catch (IOException ex)
            {
                Log.Warn($"Could not save the access token, it will change next launch: {ex.Message}");
            }

            return token;
        }

        private static string GenerateToken()
        {
            const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            var builder = new StringBuilder(bytes.Length);
            foreach (byte value in bytes)
            {
                builder.Append(Alphabet[value % Alphabet.Length]);
            }

            return builder.ToString();
        }

        /* ------------------------------------------------------------------- json */

        private string BuildHelloJson()
        {
            var json = new JsonWriter(256);
            json.StartObject();
            json.Prop("type", "hello");
            json.Prop("control", _configuration.AllowControl);
            if (_viewCapture != null)
            {
                json.StartObject("video");
                json.Prop("width", _configuration.VideoWidth);
                json.Prop("fps", _configuration.VideoFps);
                json.Prop("quality", _configuration.VideoQuality);
                json.EndObject();
            }
            else
            {
                json.PropNull("video");
            }

            json.EndObject();
            return json.ToString();
        }

        private string BuildStatusJson()
        {
            var json = new JsonWriter(256);
            json.StartObject();
            json.Prop("mod", "Juno Second Screen");
            json.Prop("port", _server?.Port ?? 0);
            json.Prop("clients", ConsoleClients);
            json.Prop("control", _configuration.AllowControl);
            json.Prop("video", _viewCapture != null);
            json.Prop("inFlight", Game.Instance.FlightScene != null);
            json.EndObject();
            return json.ToString();
        }

        private const string UnauthorizedPage =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>Juno Second Screen</title></head>" +
            "<body style=\"font-family:-apple-system,sans-serif;background:#070b12;color:#d8e3f2;padding:32px\">" +
            "<h1>Access token required</h1>" +
            "<p>Open the address printed in Juno's log, or shown on screen when a flight starts. " +
            "It looks like <code>http://192.168.x.x:8088/?t=abcd1234</code>.</p>" +
            "<p>You can turn the token off under <b>Settings &rarr; Mods &rarr; Second Screen</b>.</p>" +
            "</body></html>";

        /// <summary>
        /// One published telemetry frame. Immutable so reader threads never observe a
        /// version that does not match the payload.
        /// </summary>
        private sealed class TelemetryFrame
        {
            public TelemetryFrame(string json, int version)
            {
                Json = json;
                Version = version;
            }

            public string Json { get; }

            public int Version { get; }
        }
    }
}
