namespace PlusOne.Flight
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Threading;
    using Assets.Scripts.Craft.Parts.Modifiers;
    using PlusOne.Util;
    using UnityEngine;
    using UnityEngine.Experimental.Rendering;
    using UnityEngine.Rendering;

    /// <summary>
    /// Produces JPEG frames from a craft's camera-vantage part (nose cam,
    /// docking cam, etc.) for the console's View tab. Mirrors MfdStreamCapture's
    /// async GPU readback + background-thread JPEG pipeline; the only real
    /// difference is the capture source (a full 3D scene camera at a vantage
    /// point instead of a camera framing a flat UI canvas). Only one external
    /// camera is streamed at a time; switching the selected part tears down
    /// the old camera and sets up a new one.
    /// </summary>
    internal sealed class ExternalViewStreamCapture : IDisposable
    {
        private const int MaxFramesInFlight = 2;
        private const float AspectRatio = 16f / 9f;

        private readonly object _frameLock = new object();
        private readonly object _encodeLock = new object();
        private readonly Queue<PendingFrame> _toEncode = new Queue<PendingFrame>();
        private readonly Stack<byte[]> _bufferPool = new Stack<byte[]>();
        private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();

        private readonly int _targetWidth;
        private readonly int _quality;
        private readonly float _frameInterval;
        private readonly Func<string, CameraVantageScript> _resolveVantage;

        private ExternalCameraCapture _camera;
        private string _currentCameraName;
        private volatile string _requestedCameraName;
        private Texture2D _syncReadbackTexture;
        private Thread _encoderThread;
        private volatile bool _disposed;
        private bool _encodeOnMainThread;

        private byte[] _latestJpeg;
        private int _frameVersion;
        private int _subscribers;
        private int _framesInFlight;
        private float _nextCaptureTime;

        /// <param name="resolveVantage">
        /// Looks up the CameraVantageScript for a given camera part name. Only
        /// ever called from the main thread (from inside CaptureLoop), so it's
        /// safe for this to touch Unity/craft APIs directly.
        /// </param>
        public ExternalViewStreamCapture(int targetWidth, int fps, int quality, Func<string, CameraVantageScript> resolveVantage)
        {
            _targetWidth = Mathf.Clamp(targetWidth, 240, 1920);
            _quality = Mathf.Clamp(quality, 20, 95);
            _frameInterval = 1f / Mathf.Clamp(fps, 1, 60);
            _resolveVantage = resolveVantage;

            _encoderThread = new Thread(EncodeLoop)
            {
                IsBackground = true,
                Name = "PlusOne View JPEG",
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
        /// Requests which camera-vantage part to stream. Safe to call from any
        /// thread (e.g. an HTTP request thread) - the actual lookup and
        /// camera/GameObject setup only happen on the main thread, inside
        /// CaptureLoop.
        /// </summary>
        public void SetTargetCamera(string cameraName)
        {
            _requestedCameraName = cameraName;
        }

        // Applies a pending SetTargetCamera request. Must only be called from
        // the main thread (called from CaptureLoop, which runs as a coroutine).
        private void ApplyPendingTarget()
        {
            string cameraName = _requestedCameraName;

            // Also rebuilds if the current camera's own GameObject was
            // destroyed out from under us (e.g. the vantage part it's
            // parented to got staged/decoupled away) even though the target
            // name hasn't changed - Camera is a UnityEngine.Object, so this
            // == check correctly detects that "destroyed but not C# null"
            // state, unlike a bare null-conditional call would.
            bool stale = _camera != null && _camera.Camera == null;

            if (cameraName == _currentCameraName && _camera != null && !stale)
            {
                return;
            }

            _camera?.Dispose();
            _camera = null;
            _currentCameraName = cameraName;

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

            if (string.IsNullOrEmpty(cameraName))
            {
                return;
            }

            CameraVantageScript vantage = _resolveVantage?.Invoke(cameraName);
            if (vantage == null)
            {
                return;
            }

            var camera = new ExternalCameraCapture();
            if (camera.Setup(vantage, _targetWidth, AspectRatio))
            {
                _camera = camera;
            }
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
                // while someone's actually watching this feed.
                if (!HasSubscribers)
                {
                    try
                    {
                        ApplyPendingTarget();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"External camera idle tick failed: {ex.Message}");
                    }

                    yield return null;
                    continue;
                }

                yield return _endOfFrame;

                // The whole per-tick body is guarded, not just CaptureFrame():
                // this runs as a Unity coroutine, and unlike Update(), a
                // coroutine that throws is permanently terminated - it never
                // resumes. Losing ApplyPendingTarget (e.g. to a destroyed
                // vantage part after staging/decoupling) would silently kill
                // this feed forever, requiring a full mod restart to recover.
                try
                {
                    ApplyPendingTarget();

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
                    Log.Warn($"External camera capture failed for this frame: {ex.Message}");
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
                    Log.Warn($"Could not encode a view frame: {ex.Message}");
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
                    Log.Warn($"Encoding view frames on the main thread instead: {ex.Message}");
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
