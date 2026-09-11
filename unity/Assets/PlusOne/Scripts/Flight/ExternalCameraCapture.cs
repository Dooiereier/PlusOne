namespace PlusOne.Flight
{
    using Assets.Scripts.Craft.Parts.Modifiers;
    using Assets.Scripts.Flight.GameView.Cameras;
    using System.Reflection;
    using HarmonyLib;
    using PlusOne.Util;
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

            Camera = CreateCamera("PlusOne External Camera (near)", vantagePoint, localOffset, localRotation, nearSource, fieldOfView, isNear: true);
            _farCamera = CreateCamera("PlusOne External Camera (far)", vantagePoint, localOffset, localRotation, farSource, fieldOfView, isNear: false);

            TryAttachVolkenClouds(Camera.gameObject);

            return true;
        }

        // Volken (a popular cloud-rendering mod) has its own support for
        // attaching cloud rendering to "extra" world cameras - PIP-style
        // mods, which is structurally exactly what this near/far pair is:
        // two cameras sharing one targetTexture, far camera at a lower
        // depth. But Volken only finds such cameras via its own periodic
        // scan, which explicitly requires Camera.enabled == true (see
        // VolkenUserInterface.IsExtraWorldCamera in Volken's own source) -
        // and our near/far cameras are deliberately left disabled (see
        // CreateCamera) so Unity never auto-renders them every frame; only
        // our own throttled capture loop calls Render() on them. That
        // makes Volken's scan skip this camera pair entirely, which is why
        // clouds never appeared in this feed even though everything else
        // about the setup matches what Volken expects.
        //
        // Attaching Volken's CloudRenderer component here directly
        // sidesteps that scan, without touching our enabled=false/manual-
        // Render() setup - Unity still fires the rendering callbacks
        // CloudRenderer relies on (OnPreRender, [ImageEffectOpaque]/
        // OnRenderImage, etc.) for an explicit Camera.Render() call just
        // as it would for an auto-rendered camera. Volken resolves its own
        // far-depth source by searching for another camera sharing this
        // one's targetTexture at a lower depth (again matching our pair
        // exactly), so nothing else needs to be done once this is
        // attached.
        //
        // Uses reflection since Volken is an optional third-party mod this
        // code neither depends on nor ships with - a no-op if Volken isn't
        // installed, or if its types ever change shape.
        private static void TryAttachVolkenClouds(GameObject nearCameraObject)
        {
            try
            {
                System.Type cloudRendererType = FindVolkenType("CloudRenderer");
                if (cloudRendererType == null || nearCameraObject.GetComponent(cloudRendererType) != null)
                {
                    return;
                }

                if (!IsVolkenExtraCameraCloudsEnabled())
                {
                    return;
                }

                nearCameraObject.AddComponent(cloudRendererType);
                Log.Info("Attached Volken's cloud rendering to the external camera feed.");
            }
            catch (System.Exception ex)
            {
                Log.Warn($"Could not attach Volken clouds to the external camera feed: {ex.Message}");
            }
        }

        // Best-effort read of Volken's own "Extra Camera Clouds" player
        // setting (Assets.Scripts.ModSettings.Instance.ExtraCameraClouds.
        // Value), so this respects it the same way Volken's own scan
        // would. Defaults to true (attach anyway) if the setting can't be
        // found - Volken itself defaults it to true, and failing to read
        // an optional third-party mod's setting shouldn't silently
        // suppress the feature this whole method exists for.
        private static bool IsVolkenExtraCameraCloudsEnabled()
        {
            try
            {
                System.Type settingsType = FindVolkenType("Assets.Scripts.ModSettings");
                object instance = settingsType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                object boolSetting = instance != null ? settingsType.GetProperty("ExtraCameraClouds")?.GetValue(instance) : null;
                object value = boolSetting?.GetType().GetProperty("Value")?.GetValue(boolSetting);
                return !(value is bool enabled) || enabled;
            }
            catch
            {
                return true;
            }
        }

        private static System.Type FindVolkenType(string typeName)
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "Volken")
                {
                    return assembly.GetType(typeName);
                }
            }

            return null;
        }

        private Camera CreateCamera(string name, Transform vantagePoint, Vector3 localOffset, Quaternion localRotation, Camera sourceCamera, float? fieldOfView, bool isNear)
        {
            // CONFIRMED FIX (Player.log diagnostic showed FarCameraScript
            // never resolved onto _farCamera): HideFlags.HideAndDontSave was
            // silently excluding this GameObject from
            // UnityEngine.Object.FindObjectsOfType<Camera>() - which is
            // exactly how Volken's CloudRenderer.TryResolveFarDepthSource
            // locates its paired far camera. With no far camera found, it
            // fell back to Volken.Instance.farCam - the real game's own
            // main-view far camera - so our cloud rendering's depth/
            // occlusion data ended up tied to wherever the player's own
            // camera happened to be looking, not ours. That's why clouds
            // would partially vanish depending on the main camera's view
            // direction (confirmed: looking downward hid clouds especially).
            // HideAndDontSave is an editor-only concern anyway (hiding from
            // the Hierarchy window, skipping scene-file serialization) -
            // meaningless in a shipped build, so nothing is lost by leaving
            // it off.
            var cameraObject = new GameObject(name);
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
            else
            {
                // CONFIRMED FIX (Player.log diagnostic showed the copied far
                // camera's clearFlags was Depth, not Skybox): in the real
                // game, "Depth" is fine on the far camera because something
                // else earlier in that frame's normal render pipeline already
                // cleared the actual screen buffer being drawn to. Here, our
                // far camera is the ONLY thing that ever touches our own
                // dedicated RenderTexture - nothing else clears it, ever,
                // after it's first created. With clearFlags copied as Depth,
                // the color buffer was never cleared at all: every frame's
                // skybox/atmosphere draw layered on top of every previous
                // frame's, with nothing resetting it - exactly a gradual,
                // ever-increasing brightness accumulation toward solid white,
                // confirmed happening in every view direction and regardless
                // of Volken. The near camera's Depth-only clear stays as-is;
                // that part is correct by design (it's meant to layer onto
                // the far pass, not erase it) - only the far pass, which owns
                // clearing the color buffer for this texture, needs fixing.
                camera.clearFlags = CameraClearFlags.Skybox;
            }

            // The real camera's output normally passes through a post-process
            // stack (ImageEffectsScript -> a Beautify component: tone mapping,
            // bloom, a color grade) that CopyFrom has no way to bring along -
            // it only copies Camera-component properties, not other
            // components on that GameObject.
            //
            // A version of this once tried adding our own Beautify instance
            // and copying its properties over (including "lut", the property
            // that actually applies its tone-mapping curve) and turning HDR
            // on to match. That made things measurably worse, not better: on
            // a real test it produced a near-total white blowout of the sky.
            // The likely reason is that "lut" mirrors the *player's own*
            // tonemapping quality setting, which is commonly "None" - so lut
            // came back false, nothing was left to compress the HDR range
            // back down, and unclamped HDR values blew straight to white
            // instead of being clamped the way flat LDR rendering naturally
            // does. Reverted: HDR stays off, and this renders flatter than
            // the real, fully graded view, but safely so.
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
