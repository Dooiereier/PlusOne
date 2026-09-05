namespace JunoSecondScreen
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
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
        private readonly PlanetMapCache _planetMapCache = new PlanetMapCache();
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
        private float _nextPlanetMapCheckTime;

        // Generating the planet map is a synchronous, main-thread-only
        // engine call (PlanetCubemapUtility.CreateEquirectangularMap) that
        // turned out to cost a noticeable stutter - a second or two of the
        // whole game freezing was traced to it running the instant the
        // server started, whether or not anyone had ever opened the Orbit
        // tab. Set from ServePlanetMap (a background connection thread) the
        // first time a client actually asks for /planetmap.png; read from
        // RefreshPlanetMap (main thread) to gate generation on that instead
        // of on the server simply being on - so the cost lands when someone
        // opens the tab, not when the mod is enabled.
        private volatile bool _planetMapRequested;
        private float _nextConfigurationCheck;
        private int _consoleClients;

        // How many connected consoles currently have their MFD tab open -
        // same idea as _planetMapRequested above, but per-connection rather
        // than sticky/one-way, since a client can leave the tab again. Gates
        // MfdCollector's expensive full-widget-tree walk (see its own doc
        // comment) so it's only paid while someone can actually see it,
        // rather than on every connected client regardless of their tab.
        private int _mfdTabViewers;

        // Same idea as _mfdTabViewers, for the Orbit tab's ground-track
        // sampling (TelemetryCollector.WriteTrajectory) - that cost is
        // already capped to twice a second, but there's no reason to pay it
        // at all while every connected client is looking at some other tab.
        private int _orbitTabViewers;

        // Per-subsystem timing breakdown for Update(), to replace guessing
        // about where an observed FPS hit actually comes from with a real
        // number - e.g. distinguishing "this MonoBehaviour's own polling
        // work" from "a Harmony patch adding overhead to a game method this
        // never touches directly" (which this diagnostic would show as *no*
        // added cost here, despite a real FPS drop). Logged as an average
        // per-frame ms cost over the last UpdateDiagIntervalSeconds, not per
        // frame, so it stays readable.
        private const float UpdateDiagIntervalSeconds = 3f;
        private readonly Stopwatch _diagStopwatch = new Stopwatch();
        private double _diagCommandsMs, _diagTelemetryMs, _diagMfdMs, _diagAnnounceMs, _diagPlanetMapMs;
        private int _diagFrameCount;
        private float _nextUpdateDiagAt = float.NegativeInfinity;

        // FrameTimingManager reports actual GPU frame time (on D3D11/D3D12/
        // Metal/Vulkan) without needing the Profiler's GPU module, which
        // isn't available in this project. This is what actually answers
        // "is the GPU doing more work / taking longer per frame with the mod
        // on", rather than more CPU-side Stopwatch timing that's already
        // shown this mod's own code isn't the one spending the time.
        private readonly UnityEngine.FrameTiming[] _diagFrameTimings = new UnityEngine.FrameTiming[1];
        private double _diagGpuMsSum, _diagCpuMsSum;
        private int _diagGpuSampleCount;
        private bool _configured;
        private bool _flightMessageShown;

        // See ConnectionAddress's own remarks - this cache is what turns a
        // per-frame NetworkInterface.GetAllNetworkInterfaces() call (very
        // expensive) into a call made at most once every few seconds.
        private const float ConnectionAddressRefreshSeconds = 3f;
        private string _cachedConnectionAddress;
        private float _nextConnectionAddressRefreshAt = float.NegativeInfinity;

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
        /// <remarks>
        /// This turned out to be THE cause of a large, reliable FPS drop that
        /// survived every other fix this mod tried (disabling video capture,
        /// removing Application.runInBackground, even bypassing the HTTP
        /// listener entirely) - because none of those touch this property at
        /// all. The flight panel's address label reads this every UI refresh
        /// via its UpdateAction, and NetworkUtil.GetLocalAddresses() calls
        /// NetworkInterface.GetAllNetworkInterfaces(), a notoriously expensive
        /// .NET API (it queries the OS network stack across every adapter,
        /// including virtual ones from VPNs/Hyper-V/virtual machines) - fine
        /// once, ruinous read fresh on every single frame. The address can't
        /// meaningfully change more than once in a while anyway (network
        /// adapters don't come and go mid-flight), so it's cached here and
        /// only recomputed on a slow timer instead.
        /// </remarks>
        public string ConnectionAddress
        {
            get
            {
                if (_server == null || !_server.IsRunning)
                {
                    return null;
                }

                if (_cachedConnectionAddress == null || Time.unscaledTime >= _nextConnectionAddressRefreshAt)
                {
                    _nextConnectionAddressRefreshAt = Time.unscaledTime + ConnectionAddressRefreshSeconds;

                    string firstAddress = null;
                    foreach (string address in NetworkUtil.GetLocalAddresses())
                    {
                        firstAddress = address;
                        break;
                    }

                    string query = _configuration.RequireToken ? "/?t=" + _token : "/";
                    _cachedConnectionAddress = firstAddress != null ? $"http://{firstAddress}:{_server.Port}{query}" : null;
                }

                return _cachedConnectionAddress;
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
                        Log.Info("Settings changed.");
                    }

                    _configured = true;
                    ApplyConfiguration(current);
                }
            }

            // The flight-panel toggle (the only thing that starts the server -
            // see ApplyConfiguration/ToggleEnabled) only exists inside the
            // flight scene's own inspector panel, so a server left running
            // while the player backs out to the main menu / vehicle builder
            // has no way to be turned off again until another flight starts -
            // and there's no craft to serve telemetry for out there anyway.
            // Force it off the moment the flight scene itself goes away,
            // same as if the player had used the toggle themselves.
            if (IsRunning && Game.Instance.FlightScene == null)
            {
                Shutdown();
                Log.Info("Second screen turned off - left the flight scene.");
            }

            bool serverRunning = _server != null && _server.IsRunning;

            // Real GPU/CPU frame time from the graphics driver itself (D3D11/
            // D3D12/Metal/Vulkan), independent of anything this mod's own
            // code does. Captured every frame regardless of serverRunning -
            // unlike the subsystem timings below, which are meaningless
            // while the server's off - so the log directly compares mod-on
            // vs mod-off GPU cost without needing a separate baseline
            // reading from something else (an FPS overlay, etc.).
            UnityEngine.FrameTimingManager.CaptureFrameTimings();
            if (UnityEngine.FrameTimingManager.GetLatestTimings((uint)_diagFrameTimings.Length, _diagFrameTimings) > 0)
            {
                _diagGpuMsSum += _diagFrameTimings[0].gpuFrameTime;
                _diagCpuMsSum += _diagFrameTimings[0].cpuFrameTime;
                _diagGpuSampleCount++;
            }

            if (Time.unscaledTime >= _nextUpdateDiagAt)
            {
                _nextUpdateDiagAt = Time.unscaledTime + UpdateDiagIntervalSeconds;

                string gpuInfo = _diagGpuSampleCount > 0
                    ? $"gpuFrameTime={_diagGpuMsSum / _diagGpuSampleCount:F2}ms cpuFrameTime={_diagCpuMsSum / _diagGpuSampleCount:F2}ms (n={_diagGpuSampleCount})"
                    : "gpuFrameTime=unavailable (FrameTimingManager unsupported here)";

                if (_diagFrameCount > 0)
                {
                    double totalMs = _diagCommandsMs + _diagTelemetryMs + _diagMfdMs + _diagAnnounceMs + _diagPlanetMapMs;
                    Log.Info(
                        $"[Vizzy perf diag] serverRunning=true, Update() over {_diagFrameCount} frames / {UpdateDiagIntervalSeconds:F0}s: " +
                        $"commands={_diagCommandsMs:F1}ms telemetry={_diagTelemetryMs:F1}ms mfd={_diagMfdMs:F1}ms " +
                        $"announce={_diagAnnounceMs:F1}ms planetMap={_diagPlanetMapMs:F1}ms " +
                        $"| avg/frame={totalMs / _diagFrameCount:F3}ms | {gpuInfo} " +
                        $"consoleClients={ConsoleClients} mfdTabViewers={Volatile.Read(ref _mfdTabViewers)}");
                }
                else
                {
                    Log.Info($"[Vizzy perf diag] serverRunning=false | {gpuInfo}");
                }

                _diagCommandsMs = _diagTelemetryMs = _diagMfdMs = _diagAnnounceMs = _diagPlanetMapMs = 0d;
                _diagGpuMsSum = _diagCpuMsSum = 0d;
                _diagGpuSampleCount = 0;
                _diagFrameCount = 0;
            }

            if (!serverRunning)
            {
                return;
            }

            _diagFrameCount++;
            _diagStopwatch.Restart();
            _commands.Apply();
            _diagCommandsMs += _diagStopwatch.Elapsed.TotalMilliseconds;

            _diagStopwatch.Restart();
            PublishTelemetry();
            _diagTelemetryMs += _diagStopwatch.Elapsed.TotalMilliseconds;

            _diagStopwatch.Restart();
            PublishMfd();
            _diagMfdMs += _diagStopwatch.Elapsed.TotalMilliseconds;

            _diagStopwatch.Restart();
            AnnounceInFlight();
            _diagAnnounceMs += _diagStopwatch.Elapsed.TotalMilliseconds;

            _diagStopwatch.Restart();
            RefreshPlanetMap();
            _diagPlanetMapMs += _diagStopwatch.Elapsed.TotalMilliseconds;
        }

        // Cheap when the planet hasn't changed (a dictionary lookup, no file
        // I/O) - see PlanetMapCache.Refresh - so this can run reasonably
        // often without needing to be gated on ConsoleClients like telemetry.
        private void RefreshPlanetMap()
        {
            if (!_planetMapRequested || Time.unscaledTime < _nextPlanetMapCheckTime)
            {
                return;
            }

            _nextPlanetMapCheckTime = Time.unscaledTime + 3f;

            try
            {
                _planetMapCache.Refresh(Game.Instance.FlightScene?.CraftNode?.CraftScript);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not refresh the planet map: {ex.Message}");
            }
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
        /// Turns the server on or off for this session. This is the actual
        /// day-to-day on/off switch - the mod never auto-starts on its own
        /// (see ApplyConfiguration), so a player uses this every time they
        /// actually want the console running, same as any other in-flight
        /// toggle. Only works at all while the mod settings' Enabled checkbox
        /// permits it - that checkbox is a master gate, not a "start serving"
        /// switch of its own.
        /// </summary>
        public void ToggleEnabled()
        {
            if (IsRunning)
            {
                Shutdown();
                Log.Info("Second screen turned off from the flight panel.");
            }
            else if (_configured && _configuration.Enabled)
            {
                StartServer(_configuration);
                Log.Info("Second screen turned on from the flight panel.");
            }
        }

        private void ApplyConfiguration(ModConfiguration configuration)
        {
            _configuration = configuration;
            _commands.ControlEnabled = configuration.AllowControl;

            if (!configuration.Enabled)
            {
                // The master gate is off - fully stop and stay stopped,
                // regardless of whatever the flight panel toggle last chose
                // this session (that toggle only works while this is true).
                Shutdown();
                Log.Info("Second screen is switched off in the mod settings.");
                return;
            }

            // Enabled merely being true does NOT start the server by itself -
            // ToggleEnabled (the flight panel switch) is the only thing that
            // does that, on purpose, each session. This just makes starting
            // it possible. If it's already running (the player turned it on,
            // then changed some other setting like video quality), restart it
            // with the new configuration rather than leaving it running with
            // stale settings; otherwise leave it off.
            if (IsRunning)
            {
                Shutdown();
                StartServer(configuration);
                Log.Info("Restarting the second screen server with the new settings.");
            }
        }

        // Starts the HTTP server unconditionally, ignoring configuration.Enabled
        // (the caller decides whether that gate applies).
        private void StartServer(ModConfiguration configuration)
        {
            // Force ConnectionAddress to recompute on next read rather than
            // reusing a value cached from a previous run (e.g. a different
            // port after a settings change).
            _cachedConnectionAddress = null;
            _nextConnectionAddressRefreshAt = float.NegativeInfinity;

            _server = new HttpServer(HandleRequest);
            if (!_server.Start(configuration.Port))
            {
                _server = null;
                return;
            }

            // This used to force Application.runInBackground on (so telemetry/
            // video kept flowing while the player was alt-tabbed watching the
            // tablet instead), but that's gone now: forcing it was measured
            // to cost real FPS even while the game window WAS focused, and -
            // separately - the player explicitly wants the game's own alt-tab
            // pause behavior left alone rather than overridden by this mod.
            // The tradeoff is accepted: the console will stop updating while
            // the game is alt-tabbed away, same as if this mod didn't touch
            // the setting at all.

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
                json = _collector.Build(_planetMapCache.MapRotationAngleDeg, Volatile.Read(ref _orbitTabViewers) > 0);
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

        // How long to hold off the widget-tree walk after an MFD click - long
        // enough to comfortably clear whatever frame(s) a page-switch takes
        // to finish rebuilding its widgets, short enough to stay unnoticeable
        // given MfdIntervalSeconds itself is already 250ms.
        private const float MfdClickSettleSeconds = 0.2f;

        private void PublishMfd()
        {
            if (ConsoleClients == 0
                || Time.unscaledTime < _nextMfdTime
                || Time.unscaledTime - _commands.LastMfdClickAt < MfdClickSettleSeconds)
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
                    _externalViewCapture?.FrameVersion ?? 0,
                    Volatile.Read(ref _mfdTabViewers) > 0);
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

                case "/planetmap.png":
                    ServePlanetMap(connection);
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

        /// <summary>
        /// Tracks this one connection's MFD-tab/Orbit-tab visibility so
        /// PublishMfd/PublishTelemetry can skip their expensive per-tab work
        /// while nobody's looking - see _mfdTabViewers/_orbitTabViewers.
        /// Distinct from HandleStreamLifecycleCommand because it needs to
        /// remember state across messages (to know whether to undo it if the
        /// connection drops without ever sending "on":false), not just react
        /// to a single one-shot action.
        /// </summary>
        private bool TryHandleTabActiveCommand(string message, ref bool mfdTabActiveForConnection, ref bool orbitTabActiveForConnection)
        {
            if (!(JsonReader.Parse(message) is Dictionary<string, object> command))
            {
                return false;
            }

            bool active = JsonReader.GetBool(command, "on");
            switch (JsonReader.GetString(command, "cmd"))
            {
                case "mfdTabActive":
                    if (active != mfdTabActiveForConnection)
                    {
                        mfdTabActiveForConnection = active;
                        Interlocked.Add(ref _mfdTabViewers, active ? 1 : -1);
                    }

                    return true;

                case "orbitTabActive":
                    if (active != orbitTabActiveForConnection)
                    {
                        orbitTabActiveForConnection = active;
                        Interlocked.Add(ref _orbitTabViewers, active ? 1 : -1);
                    }

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

            bool mfdTabActiveForConnection = false;
            bool orbitTabActiveForConnection = false;
            try
            {
                while (true)
                {
                    string message = socket.ReceiveText();
                    if (message == null)
                    {
                        break;
                    }

                    if (HandleStreamLifecycleCommand(message))
                    {
                        continue;
                    }

                    if (TryHandleTabActiveCommand(message, ref mfdTabActiveForConnection, ref orbitTabActiveForConnection))
                    {
                        continue;
                    }

                    _commands.Enqueue(message);
                }
            }
            finally
            {
                // Undoes this connection's own contribution regardless of how
                // it ends - a dropped connection (network loss, closed tab)
                // never gets the chance to send "...TabActive":false itself,
                // and without this the counters would only ever go up.
                if (mfdTabActiveForConnection)
                {
                    Interlocked.Decrement(ref _mfdTabViewers);
                }

                if (orbitTabActiveForConnection)
                {
                    Interlocked.Decrement(ref _orbitTabViewers);
                }

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

        // A plain static-image response, not a stream: the map only changes
        // when the orbited planet does, which PlanetMapCache already only
        // re-resolves periodically on the main thread - see its own remarks
        // for why the actual byte[] hand-off has to work that way.
        private void ServePlanetMap(HttpConnection connection)
        {
            _planetMapRequested = true;

            byte[] png = _planetMapCache.GetPng();
            if (png == null)
            {
                connection.RespondText(503, "text/plain; charset=utf-8", "No planet map available yet.");
                return;
            }

            connection.Respond(200, "image/png", png, new[]
            {
                new KeyValuePair<string, string>("Cache-Control", "no-cache"),
            });
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
            json.Prop("mod", "Juno Tether");
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
            "<title>Juno Tether</title></head>" +
            "<body style=\"font-family:-apple-system,sans-serif;background:#070b12;color:#d8e3f2;padding:32px\">" +
            "<h1>Access token required</h1>" +
            "<p>Open the address printed in Juno's log, or shown on screen when a flight starts. " +
            "It looks like <code>http://192.168.x.x:8088/?t=abcd1234</code>.</p>" +
            "<p>You can turn the token off under <b>Settings &rarr; Mods &rarr; Juno Tether</b>.</p>" +
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
