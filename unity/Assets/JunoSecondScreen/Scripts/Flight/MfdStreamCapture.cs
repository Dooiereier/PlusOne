namespace JunoSecondScreen.Flight
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Threading;
    using Assets.Scripts.Flight.GameView.Cameras;
    using HarmonyLib;
    using JunoSecondScreen.Util;
    using UnityEngine;
    using UnityEngine.Experimental.Rendering;
    using UnityEngine.Rendering;

    /// <summary>
    /// Produces JPEG frames from a single MFD's camera for the console's MFD
    /// tab. Mirrors ViewCapture's async GPU readback + background-thread JPEG
    /// pipeline, but the source is an MfdCameraCapture's RenderTexture instead
    /// of a screenshot of the whole game view. Only one MFD is streamed at a
    /// time; switching the selected part tears down the old camera and sets
    /// up a new one.
    /// </summary>
    internal sealed class MfdStreamCapture : IDisposable
    {
        private const int MaxFramesInFlight = 2;

        private readonly object _frameLock = new object();
        private readonly object _encodeLock = new object();
        private readonly Queue<PendingFrame> _toEncode = new Queue<PendingFrame>();
        private readonly Stack<byte[]> _bufferPool = new Stack<byte[]>();
        private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();

        private readonly int _targetWidth;
        private readonly int _quality;
        private readonly float _frameInterval;
        private readonly Func<string, Canvas> _resolveCanvas;

        private MfdCameraCapture _camera;
        private Canvas _currentCanvas;
        private string _currentPart;
        private volatile string _requestedPart;
        private float _nextDiagTime;
        private Texture2D _syncReadbackTexture;
        private Thread _encoderThread;
        private volatile bool _disposed;
        private bool _encodeOnMainThread;

        private byte[] _latestJpeg;
        private int _frameVersion;
        private int _subscribers;
        private int _framesInFlight;
        private float _nextCaptureTime;

        /// <param name="resolveCanvas">
        /// Looks up the Canvas for a given MFD part name. Only ever called
        /// from the main thread (from inside CaptureLoop), so it's safe for
        /// this to touch Unity/craft APIs directly.
        /// </param>
        public MfdStreamCapture(int targetWidth, int fps, int quality, Func<string, Canvas> resolveCanvas)
        {
            _targetWidth = Mathf.Clamp(targetWidth, 240, 1920);
            _quality = Mathf.Clamp(quality, 20, 95);
            _frameInterval = 1f / Mathf.Clamp(fps, 1, 60);
            _resolveCanvas = resolveCanvas;

            _encoderThread = new Thread(EncodeLoop)
            {
                IsBackground = true,
                Name = "SecondScreen MFD JPEG",
            };
            _encoderThread.Start();
        }

        public bool HasSubscribers => Volatile.Read(ref _subscribers) > 0;

        /// <summary>
        /// Increments every time a new frame is published. Let the console
        /// detect a stalled feed (the counter stops moving) independently of
        /// whether the underlying HTTP connection itself is still open.
        /// </summary>
        public int FrameVersion => _frameVersion;

        public void AddSubscriber()
        {
            Interlocked.Increment(ref _subscribers);
        }

        public void RemoveSubscriber()
        {
            Interlocked.Decrement(ref _subscribers);
        }

        /// <summary>
        /// Requests which MFD (by part name) to stream. Safe to call from any
        /// thread (e.g. an HTTP request thread) — the actual canvas lookup and
        /// camera/GameObject setup only happen on the main thread, inside
        /// CaptureLoop.
        /// </summary>
        public void SetTargetPart(string partName)
        {
            _requestedPart = partName;
        }

        // Applies a pending SetTargetPart request. Must only be called from
        // the main thread (called from CaptureLoop, which runs as a coroutine).
        private void ApplyPendingTarget()
        {
            string partName = _requestedPart;

            // Also rebuilds if the current MFD's own canvas/camera was
            // destroyed out from under us (e.g. the part got staged/
            // decoupled away) even though the target name hasn't changed -
            // Canvas/Camera are UnityEngine.Objects, so these == checks
            // correctly detect that "destroyed but not C# null" state,
            // unlike a bare null-conditional call would.
            bool stale = _camera != null && (_camera.Camera == null || _currentCanvas == null);

            if (partName == _currentPart && _camera != null && !stale)
            {
                return;
            }

            _camera?.Dispose();
            _camera = null;
            _currentCanvas = null;
            _currentPart = partName;

            // Disposing the old camera destroys its RenderTexture, and if an
            // AsyncGPUReadback request was still in flight against it at that
            // exact moment, Unity does not reliably guarantee its completion
            // callback still fires - which is the only place this gets
            // decremented. A single dropped callback across enough target
            // switches eventually pins this at MaxFramesInFlight forever,
            // permanently blocking every future capture (for any target,
            // since this is shared, not per-target) even though nothing else
            // about the stream looks unhealthy. Any reply that does arrive
            // late for the now-destroyed texture is harmless to ignore.
            _framesInFlight = 0;

            if (string.IsNullOrEmpty(partName))
            {
                return;
            }

            Canvas canvas = _resolveCanvas?.Invoke(partName);
            if (canvas == null)
            {
                return;
            }

            var camera = new MfdCameraCapture();
            if (camera.Setup(canvas, _targetWidth, _targetWidth))
            {
                _camera = camera;
                _currentCanvas = canvas;
            }
        }

        // Throttled (every couple seconds) so it doesn't spam the log - reports
        // the MFD canvas's own active/enabled state and its distance from the
        // player's real camera, to check whether "MFD turns grey far away" is
        // the game disabling/hiding the canvas itself based on the player's
        // own camera distance (unrelated to our dedicated close-up capture
        // camera, which never moves away from the MFD).
        private void LogDiagnostics()
        {
            if (Time.unscaledTime < _nextDiagTime || _currentCanvas == null)
            {
                return;
            }

            _nextDiagTime = Time.unscaledTime + 2f;

            var cameraManager = CameraManagerScript.Instance;
            Camera realCamera = cameraManager != null ? Traverse.Create(cameraManager).Field("_nearCamera").GetValue<Camera>() : null;
            float distance = realCamera != null
                ? Vector3.Distance(realCamera.transform.position, _currentCanvas.transform.position)
                : -1f;

            Log.Info(
                $"[Vizzy MFD grey diag] canvas active={_currentCanvas.gameObject.activeInHierarchy} " +
                $"enabled={_currentCanvas.enabled} realCameraDistance={distance:0.#} " +
                $"ourCameraActive={(_camera?.Camera != null && _camera.Camera.gameObject.activeInHierarchy)} " +
                $"ancestry=[{DescribeActiveAncestry(_currentCanvas.transform)}]");
        }

        // Lists each ancestor's own activeSelf flag from the canvas up to the
        // scene root, so a canvas whose own activeSelf is true but
        // activeInHierarchy is false (inactive only because of an ancestor)
        // can be told apart from the canvas itself being the one toggled off
        // - that determines whether reactivating the canvas alone would even
        // work, or some higher, sleeping ancestor needs to be reactivated too.
        private static string DescribeActiveAncestry(Transform start)
        {
            var parts = new List<string>();
            Transform current = start;
            for (int depth = 0; current != null && depth < 12; depth++)
            {
                parts.Add($"{current.name}:{(current.gameObject.activeSelf ? "on" : "OFF")}");
                current = current.parent;
            }

            return string.Join(" < ", parts);
        }

        public bool WaitForFrame(int lastVersion, int timeoutMs, out byte[] jpeg, out int version)
        {
            lock (_frameLock)
            {
                if (_frameVersion == lastVersion)
                {
                    Monitor.Wait(_frameLock, timeoutMs);
                }

                jpeg = _latestJpeg;
                version = _frameVersion;
                return jpeg != null && version != lastVersion;
            }
        }

        public IEnumerator CaptureLoop()
        {
            while (!_disposed)
            {
                // See ViewCapture.CaptureLoop for why this is conditional:
                // WaitForEndOfFrame's render-pipeline sync point isn't worth
                // paying for every frame the mod is merely enabled - only
                // while someone's actually watching this MFD.
                if (!HasSubscribers)
                {
                    // Guarded for the same reason as the try block below - an
                    // unhandled exception here would permanently kill this
                    // coroutine (a throwing coroutine never resumes), and
                    // this branch runs every idle frame for the entire time
                    // the mod is enabled, not just while watched.
                    try
                    {
                        ApplyPendingTarget();
                        MfdVisibilityOverride.ActiveCanvas = null;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"MFD idle tick failed: {ex.Message}");
                    }

                    yield return null;
                    continue;
                }

                yield return _endOfFrame;

                // The whole per-tick body is guarded, not just CaptureFrame():
                // this runs as a Unity coroutine, and unlike Update(), a
                // coroutine that throws is permanently terminated - it never
                // resumes. Losing ApplyPendingTarget (e.g. to a destroyed MFD
                // canvas after staging/decoupling) would silently kill this
                // feed forever, requiring a full mod restart to recover.
                try
                {
                    ApplyPendingTarget();
                    LogDiagnostics();

                    // Keeps the game from putting this specific MFD's screen
                    // to sleep for as long as someone's watching it - see
                    // MfdScreenVisibilityPatch for why this is done as a
                    // patched decision rather than forcing SetActive(true)
                    // reactively.
                    MfdVisibilityOverride.ActiveCanvas = HasSubscribers ? _currentCanvas : null;

                    if (!HasSubscribers || _camera?.Texture == null)
                    {
                        continue;
                    }

                    if (Time.unscaledTime < _nextCaptureTime || _framesInFlight >= MaxFramesInFlight)
                    {
                        continue;
                    }

                    _nextCaptureTime = Time.unscaledTime + _frameInterval;
                    CaptureFrame();
                }
                catch (Exception ex)
                {
                    Log.Warn($"MFD capture failed for this frame: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_encodeLock)
            {
                Monitor.PulseAll(_encodeLock);
            }

            lock (_frameLock)
            {
                Monitor.PulseAll(_frameLock);
            }

            _encoderThread = null;
            _camera?.Dispose();
            _camera = null;
            _currentCanvas = null;
            MfdVisibilityOverride.ActiveCanvas = null;

            if (_syncReadbackTexture != null)
            {
                UnityEngine.Object.Destroy(_syncReadbackTexture);
                _syncReadbackTexture = null;
            }
        }

        private void CaptureFrame()
        {
            _camera.RenderFrame();
            RenderTexture source = _camera.Texture;
            int width = source.width;
            int height = source.height;

            if (SystemInfo.supportsAsyncGPUReadback)
            {
                _framesInFlight++;
                AsyncGPUReadback.Request(source, 0, TextureFormat.RGBA32, OnReadbackComplete);
            }
            else
            {
                ReadBackSynchronously(source, width, height);
            }
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _framesInFlight = Mathf.Max(0, _framesInFlight - 1);
            if (_disposed || request.hasError)
            {
                return;
            }

            var data = request.GetData<byte>();
            byte[] buffer = Rent(data.Length);
            data.CopyTo(buffer);
            Submit(new PendingFrame(buffer, request.width, request.height));
        }

        private void ReadBackSynchronously(RenderTexture source, int width, int height)
        {
            if (_syncReadbackTexture == null || _syncReadbackTexture.width != width || _syncReadbackTexture.height != height)
            {
                if (_syncReadbackTexture != null)
                {
                    UnityEngine.Object.Destroy(_syncReadbackTexture);
                }

                _syncReadbackTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            _syncReadbackTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            RenderTexture.active = previous;

            byte[] raw = _syncReadbackTexture.GetRawTextureData();
            byte[] buffer = Rent(raw.Length);
            Buffer.BlockCopy(raw, 0, buffer, 0, raw.Length);
            Submit(new PendingFrame(buffer, width, height));
        }

        private void Submit(PendingFrame frame)
        {
            if (_encodeOnMainThread)
            {
                try
                {
                    PublishEncoded(frame);
                }
                catch (Exception ex)
                {
                    Return(frame.Buffer);
                    Log.Warn($"Could not encode an MFD frame: {ex.Message}");
                }

                return;
            }

            lock (_encodeLock)
            {
                while (_toEncode.Count > 0)
                {
                    Return(_toEncode.Dequeue().Buffer);
                }

                _toEncode.Enqueue(frame);
                Monitor.Pulse(_encodeLock);
            }
        }

        private void EncodeLoop()
        {
            while (!_disposed)
            {
                PendingFrame frame;
                lock (_encodeLock)
                {
                    while (_toEncode.Count == 0)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        Monitor.Wait(_encodeLock, 250);
                    }

                    frame = _toEncode.Dequeue();
                }

                try
                {
                    PublishEncoded(frame);
                }
                catch (Exception ex)
                {
                    Return(frame.Buffer);
                    _encodeOnMainThread = true;
                    Log.Warn($"Encoding MFD frames on the main thread instead: {ex.Message}");
                    return;
                }
            }
        }

        private void PublishEncoded(PendingFrame frame)
        {
            byte[] jpeg = ImageConversion.EncodeArrayToJPG(
                frame.Buffer,
                GraphicsFormat.R8G8B8A8_UNorm,
                (uint)frame.Width,
                (uint)frame.Height,
                0,
                _quality);

            Return(frame.Buffer);

            lock (_frameLock)
            {
                _latestJpeg = jpeg;
                _frameVersion++;
                Monitor.PulseAll(_frameLock);
            }
        }

        private byte[] Rent(int size)
        {
            lock (_bufferPool)
            {
                while (_bufferPool.Count > 0)
                {
                    byte[] buffer = _bufferPool.Pop();
                    if (buffer.Length == size)
                    {
                        return buffer;
                    }
                }
            }

            return new byte[size];
        }

        private void Return(byte[] buffer)
        {
            if (buffer == null)
            {
                return;
            }

            lock (_bufferPool)
            {
                if (_bufferPool.Count < 4)
                {
                    _bufferPool.Push(buffer);
                }
            }
        }

        private readonly struct PendingFrame
        {
            public PendingFrame(byte[] buffer, int width, int height)
            {
                Buffer = buffer;
                Width = width;
                Height = height;
            }

            public byte[] Buffer { get; }

            public int Width { get; }

            public int Height { get; }
        }
    }
}
