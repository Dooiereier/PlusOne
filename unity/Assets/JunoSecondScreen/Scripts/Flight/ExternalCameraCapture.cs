namespace JunoSecondScreen.Flight
{
    using Assets.Scripts.Craft.Parts.Modifiers;
    using Assets.Scripts.Flight.GameView.Cameras;
    using HarmonyLib;
    using UnityEngine;

    /// <summary>
    /// Attaches a pair of dedicated cameras at a craft camera-vantage part's
    /// own camera point (nose cam, docking cam, etc.) for the console's View
    /// tab. Unlike MfdCameraCapture (which frames a flat UI canvas), this
    /// renders the real 3D scene - and like the game's own camera system, it
    /// actually needs two cameras to do that: SR2 splits rendering across a
    /// "near" camera (nearby geometry) and a "far" camera (skybox and distant
    /// terrain/planets, drawn first so the near pass can layer on top without
    /// clearing it). CameraManagerScript keeps both as private fields
    /// (_nearCamera/_farCamera), so Harmony's Traverse is used to reach them;
    /// their render settings (culling mask, clip planes, clear flags) are
    /// copied onto our own pair rather than derived from a canvas rect.
    /// </summary>
    internal sealed class ExternalCameraCapture
    {
        private const float DefaultNearClip = 0.05f;
        private const float DefaultFarClip = 750000f; // SR2 scenes span planetary distances

        private Camera _farCamera;

        /// <summary>
        /// The near-field camera. Its render, run second, is what actually
        /// lands in <see cref="Texture"/> on top of the far camera's pass.
        /// </summary>
        public Camera Camera { get; private set; }

        public RenderTexture Texture { get; private set; }

        /// <summary>
        /// Creates the camera pair as children of the vantage part's own
        /// camera point, offset by the part's own CameraOffset/RotationOffset
        /// so they sit exactly where the game's own camera system would place
        /// them (and follow it through floating-origin resets, since they
        /// move with the same transform hierarchy operation).
        /// </summary>
        public bool Setup(CameraVantageScript vantage, int textureWidth, float aspectRatio)
        {
            Transform vantagePoint = vantage != null ? vantage.CameraPosition : null;
            if (vantagePoint == null || textureWidth <= 0 || aspectRatio <= 0f)
            {
                return false;
            }

            var cameraManager = CameraManagerScript.Instance;
            Camera nearSource = cameraManager != null ? Traverse.Create(cameraManager).Field("_nearCamera").GetValue<Camera>() : null;
            Camera farSource = cameraManager != null ? Traverse.Create(cameraManager).Field("_farCamera").GetValue<Camera>() : null;

            // CameraPosition looks like a shared mount point on the part (the
            // same for every vantage), with CameraOffset/CameraRotationOffset
            // being the per-vantage data that actually distinguishes e.g. a
            // nose cam from a docking cam - without applying them, every
            // vantage would render the exact same view from the same spot.
            Vector3 localOffset = vantage.Data != null ? vantage.Data.CameraOffset : Vector3.zero;
            Quaternion localRotation = vantage.Data != null ? Quaternion.Euler(vantage.Data.CameraRotationOffset) : Quaternion.identity;

            // Only override the copied camera's FOV when the part actually
            // specifies one - an unset (0) value isn't a real design choice
            // to render at.
            float? fieldOfView = vantage.Data != null && vantage.Data.FieldOfView > 0f ? vantage.Data.FieldOfView : (float?)null;

            int height = Mathf.Max(1, Mathf.RoundToInt(textureWidth / aspectRatio));
            Texture = new RenderTexture(textureWidth, height, 24);

            Camera = CreateCamera("SecondScreen External Camera (near)", vantagePoint, localOffset, localRotation, nearSource, fieldOfView, isNear: true);
            _farCamera = CreateCamera("SecondScreen External Camera (far)", vantagePoint, localOffset, localRotation, farSource, fieldOfView, isNear: false);

            return true;
        }

        private Camera CreateCamera(string name, Transform vantagePoint, Vector3 localOffset, Quaternion localRotation, Camera sourceCamera, float? fieldOfView, bool isNear)
        {
            var cameraObject = new GameObject(name)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var camera = cameraObject.AddComponent<Camera>();

            // Match the source camera's rendering settings (culling mask, clip
            // planes, clear flags/skybox, HDR, etc.) so the feed looks like a
            // real in-game view rather than a bare default camera. Falls back
            // to sane "near" defaults if that camera can't be found.
            if (sourceCamera != null)
            {
                camera.CopyFrom(sourceCamera);
            }
            else
            {
                camera.nearClipPlane = DefaultNearClip;
                camera.farClipPlane = DefaultFarClip;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.cullingMask = ~0;
            }

            // CopyFrom also copies the source's viewport rect/orthographic,
            // which don't apply here - both cameras always fill the whole
            // texture. FOV is only overridden when the part specifies its
            // own; otherwise the copied (real near/far camera) FOV stands.
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.orthographic = false;
            if (fieldOfView.HasValue)
            {
                camera.fieldOfView = fieldOfView.Value;
            }

            // The copied near clip plane is tuned for the real near camera,
            // which normally sits several meters from the craft in a chase/
            // external view. A vantage camera mounted directly on the hull is
            // often much closer than that to its own nearby parts, so reusing
            // that clip distance clips them out of view entirely - keep our
            // own small, vantage-appropriate near clip instead.
            if (isNear)
            {
                camera.nearClipPlane = DefaultNearClip;
            }

            // The real camera's output normally passes through a post-process
            // stack (ImageEffectsScript: tone mapping, bloom, a Beautify color
            // grade) that CopyFrom has no way to bring along - it only copies
            // Camera-component properties, not other components on that
            // GameObject. Without tone mapping, HDR sky/highlight values that
            // the real pipeline compresses back into range instead clip
            // straight to white (the "sky fades to white" look). Rendering in
            // LDR here avoids that overflow in the first place, at the cost
            // of looking flatter than the real, fully graded view.
            camera.allowHDR = false;

            camera.targetTexture = Texture; // off-screen only, never drawn to the player's screen
            camera.enabled = false; // we call Render() manually from the capture loop, not every engine frame

            // Applied last: CopyFrom (or possibly adding the Camera component
            // itself) was observed to move/reset this GameObject's transform,
            // despite Unity's documentation saying CopyFrom only touches
            // Camera-component properties - empirically it does not survive
            // being set before CopyFrom, so it's set again here to win.
            cameraObject.transform.SetParent(vantagePoint, false);
            cameraObject.transform.localPosition = localOffset;
            cameraObject.transform.localRotation = localRotation;
            camera.depth = sourceCamera != null ? sourceCamera.depth : 0f;

            return camera;
        }

        /// <summary>
        /// Renders one frame into <see cref="Texture"/> - far pass first (sky
        /// and distant terrain, clearing the texture), then near pass on top
        /// (its clear flags are normally Depth-only, so it layers rather than
        /// erasing the far pass), mirroring the game's own compositing order.
        /// </summary>
        public void RenderFrame()
        {
            _farCamera?.Render();
            Camera?.Render();
        }

        public void Dispose()
        {
            if (Camera != null)
            {
                Object.Destroy(Camera.gameObject);
                Camera = null;
            }

            if (_farCamera != null)
            {
                Object.Destroy(_farCamera.gameObject);
                _farCamera = null;
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
