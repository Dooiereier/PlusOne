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
    using ModApi.Craft;
    using UnityEngine;

    /// <summary>
    /// The mod's runtime: owns the HTTP server, publishes telemetry frames to
    /// connected tablets and feeds their commands back into the flight scene.
    /// </summary>
    internal sealed class SecondScreenService : MonoBehaviour
    {
        private const string TokenFileName = "SecondScreenToken.txt";
        private const int TelemetryWaitMs = 1000;
        private const float MfdIntervalSeconds = 0.25f; // 4 Hz; MFD trees are heavier than telemetry, so a slower, fixed cadence

        private readonly TelemetryCollector _collector = new TelemetryCollector();
        private readonly MfdCollector _mfdCollector = new MfdCollector();
        private readonly CameraVantageCollector _cameraCollector = new CameraVantageCollector();
        private readonly CommandProcessor _commands = new CommandProcessor();
        private readonly object _telemetrySignal = new object();

        // Switching MFDs/cameras repeatedly can leave old MJPEG connections
        // open on the browser's side indefinitely - some browsers do not
        // reliably send a TCP close when an <img> consuming a
        // multipart/x-mixed-replace stream is replaced, so the server has no
        // graceful-close signal to ever detect. Since only one target is
        // ever "current" per stream anyway, each tracker proactively closes
        // its own oldest connection once too many pile up, rather than
        // waiting to notice one is dead (which, per the above, may never
        // happen) - this is what actually keeps the global 12-connection cap
        // from being exhausted by nothing but switching around.
        private readonly ConnectionTracker _viewConnections = new ConnectionTracker(3);
        private readonly ConnectionTracker _mfdConnections = new ConnectionTracker(3);
        private readonly ConnectionTracker _externalViewConnections = new ConnectionTracker(3);

        private HttpServer _server;
        private ViewCapture _viewCapture;
        private MfdStreamCapture _mfdCapture;
        private ExternalViewStreamCapture _externalViewCapture;
        private TelemetryFrame _frame;
        private TelemetryFrame _mfdFrame;
        private ModConfiguration _configuration;
        private string _token = string.Empty;
        private string _persistentDataPath;
        private float _nextTelemetryTime;
        private float _nextMfdTime;
        private float _nextConfigurationCheck;
        private int _consoleClients;
        private bool _configured;
        private bool _flightMessageShown;
        private bool _runInBackgroundBeforeStart;

        /// <summary>
        /// The single running instance, so external UI code (e.g. the flight
        /// panel button) can reach the service without its own reference.
        /// </summary>
        public static SecondScreenService Instance { get; private set; }

        /// <summary>
        /// Whether the server is currently accepting connections.
        /// </summary>
        public bool IsRunning => _server != null && _server.IsRunning;

        /// <summary>
        /// The first connection URL (address + port + token query string, no
        /// "Second screen: " label), or null if the server isn't running or no
        /// local network address was found. Kept short/clean for display in UI,
        /// unlike BuildConnectionInfo()'s log-formatted lines.
        /// </summary>
        public string ConnectionAddress
        {
            get
            {
                if (_server == null || !_server.IsRunning)
                {
                    return null;
                }

                string firstAddress = null;
                foreach (string address in NetworkUtil.GetLocalAddresses())
                {
                    firstAddress = address;
                    break;
                }

                if (firstAddress == null)
                {
                    return null;
                }

                string query = _configuration.RequireToken ? "/?t=" + _token : "/";
                return $"http://{firstAddress}:{_server.Port}{query}";
            }
        }

        /// <summary>
        /// Gets the number of connected consoles.
        /// </summary>
        private int ConsoleClients => Volatile.Read(ref _consoleClients);

        private void Awake()
        {
            Instance = this;

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
            PublishMfd();
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

        /// <summary>
        /// Toggles the server on/off for this session, independent of the mod
        /// settings screen's Enabled checkbox. Reuses the rest of the last read
        /// configuration (port, video options, etc.) to start, so this only
        /// does something useful after the first configuration read. If the
        /// mod settings' Enabled value changes afterwards, that takes over
        /// again on the next settings check as usual.
        /// </summary>
        public void ToggleEnabled()
        {
            if (IsRunning)
            {
                Shutdown();
                Log.Info("Second screen turned off from the flight panel.");
            }
            else if (_configured)
            {
                Shutdown();
                _commands.ControlEnabled = _configuration.AllowControl;
                StartServer(_configuration);
                Log.Info("Second screen turned on from the flight panel.");
            }
        }

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

            StartServer(configuration);
        }

        // Starts the HTTP server unconditionally, ignoring configuration.Enabled
        // (the caller decides whether that gate applies).
        private void StartServer(ModConfiguration configuration)
        {
            _server = new HttpServer(HandleRequest);
            if (!_server.Start(configuration.Port))
            {
                _server = null;
                return;
            }

            // Unity throttles/pauses the main loop - including every
            // coroutine, which is what the whole capture pipeline (target
            // switching, camera rendering, frame encoding) runs on - once the
            // game window loses focus, unless the app opts out of that. The
            // whole point of a second screen is being watched from a tablet/
            // browser while the player may not have the game window focused,
            // so that default would silently stall every feed each time.
            // Restored on Shutdown() rather than left on permanently, so this
            // mod only changes that behavior while it's actually running.
            _runInBackgroundBeforeStart = Application.runInBackground;
            Application.runInBackground = true;

            if (configuration.VideoEnabled)
            {
                _viewCapture = new ViewCapture(configuration.VideoWidth, configuration.VideoFps, configuration.VideoQuality);
                StartCoroutine(_viewCapture.CaptureLoop());

                // Reuses the video settings (width/fps/quality); the MFD tab
                // is a separate stream but doesn't need its own settings yet.
                // The resolver runs on the main thread (from inside the
                // capture's own coroutine), so it's safe to touch craft/Unity
                // APIs here.
                _mfdCapture = new MfdStreamCapture(
                    configuration.VideoWidth,
                    configuration.VideoFps,
                    configuration.VideoQuality,
                    partName => MfdCollector.FindCanvas(partName, Game.Instance.FlightScene?.CraftNode?.CraftScript));
                StartCoroutine(_mfdCapture.CaptureLoop());

                // Craft camera-vantage parts (nose cam, docking cam, etc.) as
                // an alternate, independent feed on the View tab - same
                // resolver pattern as the MFD stream above.
                _externalViewCapture = new ExternalViewStreamCapture(
                    configuration.VideoWidth,
                    configuration.VideoFps,
                    configuration.VideoQuality,
                    cameraName => _cameraCollector.Find(cameraName, Game.Instance.FlightScene?.CraftNode?.CraftScript));
                StartCoroutine(_externalViewCapture.CaptureLoop());
            }

            foreach (string line in BuildConnectionInfo())
            {
                Log.Info(line);
            }

            var address = ConnectionAddress;
            if (!string.IsNullOrEmpty(address))
            {
                Log.Info("Second screen started on " + address);
            }
        }

        private void Shutdown()
        {
            StopAllCoroutines();

            // Only restore if we actually forced it in StartServer (_server
            // is null here on e.g. the very first configuration read, before
            // this mod has ever started anything - restoring an unset,
            // default-false _runInBackgroundBeforeStart in that case would
            // incorrectly force the game's own setting off).
            if (_server != null)
            {
                Application.runInBackground = _runInBackgroundBeforeStart;
            }

            _server?.Stop();
            _server = null;

            _viewCapture?.Dispose();
            _viewCapture = null;

            _mfdCapture?.Dispose();
            _mfdCapture = null;

            _externalViewCapture?.Dispose();
            _externalViewCapture = null;

            _commands.Reset();
            _frame = null;
            _mfdFrame = null;
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

        /* -------------------------------------------------------------------- mfd */

        private void PublishMfd()
        {
            if (ConsoleClients == 0 || Time.unscaledTime < _nextMfdTime)
            {
                return;
            }

            _nextMfdTime = Time.unscaledTime + MfdIntervalSeconds;

            ICraftScript craft = Game.Instance.FlightScene?.CraftNode?.CraftScript;

            string json;
            try
            {
                json = _mfdCollector.Build(
                    craft,
                    _mfdCapture?.FrameVersion ?? 0,
                    _viewCapture?.FrameVersion ?? 0,
                    _externalViewCapture?.FrameVersion ?? 0);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read MFD widgets: {ex.Message}");
                return;
            }

            lock (_telemetrySignal)
            {
                _mfdFrame = new TelemetryFrame(json, (_mfdFrame?.Version ?? 0) + 1);
                Monitor.PulseAll(_telemetrySignal);
            }
        }

        private TelemetryFrame WaitForMfd(int lastVersion)
        {
            lock (_telemetrySignal)
            {
                if (_mfdFrame == null || _mfdFrame.Version == lastVersion)
                {
                    Monitor.Wait(_telemetrySignal, TelemetryWaitMs);
                }

                return _mfdFrame != null && _mfdFrame.Version != lastVersion ? _mfdFrame : null;
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

                case "/mfd.mjpg":
                    ServeMfdVideo(request, connection);
                    return;

                case "/camview.mjpg":
                    ServeExternalView(request, connection);
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

                    if (!HandleStreamLifecycleCommand(request.Body))
                    {
                        _commands.Enqueue(request.Body);
                    }

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

        /// <summary>
        /// Handles the console's stream-lifecycle commands (distinct from
        /// craft-control commands, so they bypass CommandProcessor entirely):
        /// the console sends one of these when it stops watching a feed
        /// (leaving the MFD/Cameras tab, pressing "Stop feed") so the server
        /// can force-close every connection still open for that stream right
        /// then, rather than waiting for a browser-side abandonment signal
        /// that has proven unreliable for these long-lived MJPEG responses.
        /// </summary>
        /// <returns><c>true</c> if the message was a stream-lifecycle command
        /// (handled here, regardless of outcome) and should not also be
        /// treated as a craft-control command.</returns>
        private bool HandleStreamLifecycleCommand(string message)
        {
            if (!(JsonReader.Parse(message) is Dictionary<string, object> command))
            {
                return false;
            }

            switch (JsonReader.GetString(command, "cmd"))
            {
                case "stopMfdFeed":
                    _mfdConnections.CloseAll();
                    return true;

                case "stopViewFeed":
                    _viewConnections.CloseAll();
                    _externalViewConnections.CloseAll();
                    return true;

                default:
                    return false;
            }
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

            var sendLock = new object();

            var pushThread = new Thread(() => PushTelemetry(socket, sendLock))
            {
                IsBackground = true,
                Name = "SecondScreen Telemetry",
            };
            pushThread.Start();

            var pushMfdThread = new Thread(() => PushMfd(socket, sendLock))
            {
                IsBackground = true,
                Name = "SecondScreen Mfd",
            };
            pushMfdThread.Start();

            try
            {
                while (true)
                {
                    string message = socket.ReceiveText();
                    if (message == null)
                    {
                        break;
                    }

                    if (!HandleStreamLifecycleCommand(message))
                    {
                        _commands.Enqueue(message);
                    }
                }
            }
            finally
            {
                socket.Close();
                pushThread.Join(500);
                pushMfdThread.Join(500);
                if (Interlocked.Decrement(ref _consoleClients) == 0)
                {
                    _commands.Reset();
                }
            }
        }

        private void PushTelemetry(WebSocketConnection socket, object sendLock)
        {
            int lastVersion = 0;
            while (socket.IsOpen && _server != null && _server.IsRunning)
            {
                TelemetryFrame frame = WaitForTelemetry(lastVersion);
                if (frame == null)
                {
                    continue;
                }

                bool sent;
                lock (sendLock)
                {
                    sent = socket.SendText(frame.Json);
                }

                if (!sent)
                {
                    return;
                }

                lastVersion = frame.Version;
            }
        }

        private void PushMfd(WebSocketConnection socket, object sendLock)
        {
            int lastVersion = 0;
            while (socket.IsOpen && _server != null && _server.IsRunning)
            {
                TelemetryFrame frame = WaitForMfd(lastVersion);
                if (frame == null)
                {
                    continue;
                }

                bool sent;
                lock (sendLock)
                {
                    sent = socket.SendText(frame.Json);
                }

                if (!sent)
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

            _viewConnections.Add(connection);
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
                _viewConnections.Remove(connection);
                capture.RemoveSubscriber();
                connection.Close();
            }
        }

        private void ServeMfdVideo(HttpRequest request, HttpConnection connection)
        {
            MfdStreamCapture capture = _mfdCapture;
            if (capture == null)
            {
                connection.RespondText(503, "text/plain; charset=utf-8", "The video feed is disabled in the mod settings.");
                return;
            }

            string partName = request.GetQuery("part");
            if (string.IsNullOrEmpty(partName))
            {
                connection.RespondText(400, "text/plain; charset=utf-8", "Missing ?part= query parameter.");
                return;
            }

            capture.SetTargetPart(partName);

            _mfdConnections.Add(connection);
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
                _mfdConnections.Remove(connection);
                capture.RemoveSubscriber();
                connection.Close();
            }
        }

        private void ServeExternalView(HttpRequest request, HttpConnection connection)
        {
            ExternalViewStreamCapture capture = _externalViewCapture;
            if (capture == null)
            {
                connection.RespondText(503, "text/plain; charset=utf-8", "The video feed is disabled in the mod settings.");
                return;
            }

            string cameraName = request.GetQuery("camera");
            if (string.IsNullOrEmpty(cameraName))
            {
                connection.RespondText(400, "text/plain; charset=utf-8", "Missing ?camera= query parameter.");
                return;
            }

            capture.SetTargetCamera(cameraName);

            _externalViewConnections.Add(connection);
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
                _externalViewConnections.Remove(connection);
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

        /// <summary>
        /// Bounds how many MJPEG connections a single stream (view/MFD/
        /// external camera) keeps open at once, closing its own oldest
        /// connection once a new one pushes it over the limit.
        /// </summary>
        /// <remarks>
        /// Some browsers do not reliably send a TCP close when an &lt;img&gt;
        /// consuming a multipart/x-mixed-replace stream is replaced with a
        /// new one (e.g. when the console switches which MFD/camera it's
        /// watching) - the old connection can be left open indefinitely from
        /// the server's point of view, with no graceful-close signal to ever
        /// detect. Closing a connection out from under a thread that is
        /// blocked writing to it reliably unblocks that thread immediately in
        /// .NET, so proactively evicting the oldest connection here is what
        /// actually keeps repeated switching from slowly exhausting
        /// HttpServer's global connection cap - waiting to notice an old
        /// connection is dead was not working in practice.
        /// </remarks>
        private sealed class ConnectionTracker
        {
            private readonly List<HttpConnection> _connections = new List<HttpConnection>();
            private readonly int _max;

            public ConnectionTracker(int max)
            {
                _max = max;
            }

            public void Add(HttpConnection connection)
            {
                lock (_connections)
                {
                    _connections.Add(connection);
                    while (_connections.Count > _max)
                    {
                        HttpConnection oldest = _connections[0];
                        _connections.RemoveAt(0);
                        oldest.Close();
                    }
                }
            }

            /// <summary>
            /// Force-closes every currently tracked connection. Eviction in
            /// Add() only fires when a *new* connection of the same stream
            /// arrives - leaving a tab (MFD, say, for Cameras) abandons the
            /// old connection without ever opening a new one of that same
            /// type, so nothing would otherwise trigger cleanup until the
            /// user happened to come back to that tab. Called explicitly when
            /// the client reports it's actually done with this stream.
            /// </summary>
            public void CloseAll()
            {
                lock (_connections)
                {
                    foreach (HttpConnection connection in _connections)
                    {
                        connection.Close();
                    }

                    _connections.Clear();
                }
            }

            public void Remove(HttpConnection connection)
            {
                lock (_connections)
                {
                    _connections.Remove(connection);
                }
            }
        }
    }
}
