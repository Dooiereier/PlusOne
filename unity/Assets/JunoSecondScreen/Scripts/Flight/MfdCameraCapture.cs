namespace JunoSecondScreen.Flight
{
    using UnityEngine;

    /// <summary>
    /// Attaches a dedicated camera as a child of an MFD's Canvas RectTransform,
    /// framed to fill as much of the MFD screen as possible. Being a child of
    /// the canvas means it moves in perfect lockstep with it, including through
    /// floating-origin resets, since Unity moves an entire parent/child
    /// hierarchy as one operation.
    ///
    /// The camera renders only to an off-screen RenderTexture (never to the
    /// player's actual display), so it is never visible in-game by itself.
    /// </summary>
    internal sealed class MfdCameraCapture
    {
        private const float NearFarPadding = 0.05f; // world meters in front/behind the canvas plane

        public Camera Camera { get; private set; }

        public RenderTexture Texture { get; private set; }

        /// <summary>
        /// Creates the camera as a child of the given canvas and points it at
        /// the canvas, sized to just fit its full rect.
        /// </summary>
        public bool Setup(Canvas canvas, int textureWidth, int textureHeight)
        {
            RectTransform rect = canvas != null ? canvas.GetComponent<RectTransform>() : null;
            if (rect == null)
            {
                return false;
            }

            float width = rect.rect.width;
            float height = rect.rect.height;
            if (width <= 0f || height <= 0f)
            {
                return false;
            }

            // rect.rect is in the RectTransform's own (UI) units, which for a
            // world-space canvas can be scaled down to world meters by a large,
            // sometimes non-uniform factor (lossyScale). Everything below works
            // in actual world-space distances derived from that scale, rather
            // than assuming scale ~= 1 - a fixed local offset/clip-plane pair
            // would otherwise land at the wrong world distance and clip the
            // canvas out of frame entirely.
            Vector3 lossyScale = rect.lossyScale;
            float scaleX = Mathf.Abs(lossyScale.x) > 0.0001f ? Mathf.Abs(lossyScale.x) : 1f;
            float scaleY = Mathf.Abs(lossyScale.y) > 0.0001f ? Mathf.Abs(lossyScale.y) : 1f;
            float scaleZ = Mathf.Abs(lossyScale.z) > 0.0001f ? Mathf.Abs(lossyScale.z) : 1f;

            float worldWidth = width * scaleX;
            float worldHeight = height * scaleY;
            if (worldWidth <= 0f || worldHeight <= 0f)
            {
                return false;
            }

            // Far enough from the canvas plane to comfortably contain it
            // regardless of its physical size.
            float worldDistance = Mathf.Max(worldWidth, worldHeight) + NearFarPadding * 2f;

            var cameraObject = new GameObject("SecondScreen MFD Camera")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            cameraObject.transform.SetParent(rect, false);

            // localPosition is in the parent's unscaled local space, so divide
            // by the parent's own Z scale to land exactly worldDistance meters
            // away in world space, whatever that scale happens to be.
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -worldDistance / scaleZ);
            cameraObject.transform.localRotation = Quaternion.identity;

            Camera = cameraObject.AddComponent<Camera>();
            Camera.orthographic = true;
            Camera.orthographicSize = worldHeight / 2f;
            Camera.nearClipPlane = Mathf.Max(0.01f, worldDistance - NearFarPadding);
            Camera.farClipPlane = worldDistance + NearFarPadding;
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Color.black;

            // Isolate to just this canvas's layer so nothing else (cockpit,
            // other parts, other MFDs) leaks into the frame.
            Camera.cullingMask = 1 << rect.gameObject.layer;

            // Size the texture to match the canvas's own aspect ratio so the
            // image isn't stretched; the caller's requested width is kept,
            // height is derived from it.
            float aspect = worldWidth / worldHeight;
            int actualHeight = Mathf.Max(1, Mathf.RoundToInt(textureWidth / aspect));

            Texture = new RenderTexture(textureWidth, actualHeight, 16);
            Camera.targetTexture = Texture; // off-screen only, never drawn to the player's screen
            Camera.enabled = false; // we call Render() manually from the capture loop, not every engine frame

            return true;
        }

        /// <summary>
        /// Renders one frame into <see cref="Texture"/>.
        /// </summary>
        public void RenderFrame()
        {
            Camera?.Render();
        }

        public void Dispose()
        {
            if (Camera != null)
            {
                Object.Destroy(Camera.gameObject);
                Camera = null;
            }

            if (Texture != null)
            {
                Texture.Release();
                Object.Destroy(Texture);
                Texture = null;
            }
        }
    }
}
