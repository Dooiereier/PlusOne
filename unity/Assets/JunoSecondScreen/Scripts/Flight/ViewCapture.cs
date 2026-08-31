namespace JunoSecondScreen.Flight
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Threading;
    using JunoSecondScreen.Util;
    using UnityEngine;
    using UnityEngine.Experimental.Rendering;
    using UnityEngine.Rendering;

    /// <summary>
    /// Produces JPEG frames of the game view for the console's video panel.
    /// </summary>
    /// <remarks>
    /// Frames are pulled off the GPU asynchronously and encoded on a worker thread so
    /// that streaming to the tablet costs the render thread as little as possible.
    /// Nothing is allocated or captured while no client is watching.
    /// </remarks>
    internal sealed class ViewCapture : IDisposable
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

        private RenderTexture _screenTexture;
        private RenderTexture _scaledTexture;
        private Texture2D _syncReadbackTexture;
        private Thread _encoderThread;
        private volatile bool _disposed;
        private bool _encodeOnMainThread;

        private byte[] _latestJpeg;
        private int _frameVersion;
        private int _subscribers;
        private int _framesInFlight;
        private float _nextCaptureTime;

        public ViewCapture(int targetWidth, int fps, int quality)
        {
            _targetWidth = Mathf.Clamp(targetWidth, 240, 1920);
            _quality = Mathf.Clamp(quality, 20, 95);
            _frameInterval = 1f / Mathf.Clamp(fps, 1, 60);

            _encoderThread = new Thread(EncodeLoop)
            {
                IsBackground = true,
                Name = "SecondScreen JPEG",
            };
            _encoderThread.Start();
        }

        /// <summary>
        /// Gets a value indicating whether at least one client is watching the feed.
        /// </summary>
        public bool HasSubscribers => Volatile.Read(ref _subscribers) > 0;

        public void AddSubscriber()
        {
            Interlocked.Increment(ref _subscribers);
        }

        public void RemoveSubscriber()
        {
            Interlocked.Decrement(ref _subscribers);
        }

        /// <summary>
        /// Waits for a frame newer than the one the caller last sent.
        /// </summary>
        /// <param name="lastVersion">The version the caller already has.</param>
        /// <param name="timeoutMs">How long to wait before giving up.</param>
        /// <param name="jpeg">The encoded frame.</param>
        /// <param name="version">The version of the returned frame.</param>
        /// <returns><c>true</c> if a newer frame became available.</returns>
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

        /// <summary>
        /// The Unity coroutine that captures the screen. Runs for the lifetime of the mod.
        /// </summary>
        public IEnumerator CaptureLoop()
        {
            while (!_disposed)
            {
                yield return _endOfFrame;

                if (!HasSubscribers)
                {
                    ReleaseTextures();
                    continue;
                }

                if (Time.unscaledTime < _nextCaptureTime || _framesInFlight >= MaxFramesInFlight)
                {
                    continue;
                }

                _nextCaptureTime = Time.unscaledTime + _frameInterval;

                try
                {
                    CaptureFrame();
                }
                catch (Exception ex)
                {
                    Log.Warn($"View capture failed for this frame: {ex.Message}");
                    ReleaseTextures();
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
            ReleaseTextures();
        }

        private void CaptureFrame()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            if (screenWidth <= 0 || screenHeight <= 0)
            {
                return;
            }

            int width = Mathf.Min(_targetWidth, screenWidth);
            int height = Mathf.Max(2, Mathf.RoundToInt(width * screenHeight / (float)screenWidth));
            EnsureTextures(screenWidth, screenHeight, width, height);

            ScreenCapture.CaptureScreenshotIntoRenderTexture(_screenTexture);

            // The captured texture is bottom-up, so flip it while downscaling.
            Graphics.Blit(_screenTexture, _scaledTexture, new Vector2(1f, -1f), new Vector2(0f, 1f));

            if (SystemInfo.supportsAsyncGPUReadback)
            {
                _framesInFlight++;
                AsyncGPUReadback.Request(_scaledTexture, 0, TextureFormat.RGBA32, OnReadbackComplete);
            }
            else
            {
                ReadBackSynchronously(width, height);
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

        private void ReadBackSynchronously(int width, int height)
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
            RenderTexture.active = _scaledTexture;
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
                    Log.Warn($"Could not encode a frame: {ex.Message}");
                }

                return;
            }

            lock (_encodeLock)
            {
                // Only the newest frame matters; drop anything the encoder fell behind on.
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
                    // Some Unity builds refuse image conversion off the main thread.
                    // Fall back to encoding inline; it costs a little main thread time.
                    Return(frame.Buffer);
                    _encodeOnMainThread = true;
                    Log.Warn($"Encoding frames on the main thread instead: {ex.Message}");
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

        private void EnsureTextures(int screenWidth, int screenHeight, int width, int height)
        {
            if (_screenTexture != null && (_screenTexture.width != screenWidth || _screenTexture.height != screenHeight))
            {
                ReleaseTextures();
            }

            if (_screenTexture == null)
            {
                _screenTexture = new RenderTexture(screenWidth, screenHeight, 0, RenderTextureFormat.ARGB32);
                _screenTexture.Create();
            }

            if (_scaledTexture != null && (_scaledTexture.width != width || _scaledTexture.height != height))
            {
                _scaledTexture.Release();
                UnityEngine.Object.Destroy(_scaledTexture);
                _scaledTexture = null;
            }

            if (_scaledTexture == null)
            {
                _scaledTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Bilinear,
                };
                _scaledTexture.Create();
            }
        }

        private void ReleaseTextures()
        {
            if (_screenTexture != null)
            {
                _screenTexture.Release();
                UnityEngine.Object.Destroy(_screenTexture);
                _screenTexture = null;
            }

            if (_scaledTexture != null)
            {
                _scaledTexture.Release();
                UnityEngine.Object.Destroy(_scaledTexture);
                _scaledTexture = null;
            }

            if (_syncReadbackTexture != null)
            {
                UnityEngine.Object.Destroy(_syncReadbackTexture);
                _syncReadbackTexture = null;
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
