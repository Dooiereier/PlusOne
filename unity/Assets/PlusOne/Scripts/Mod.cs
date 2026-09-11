namespace PlusOne
{
    using PlusOne.Flight;
    using PlusOne.Util;
    using ModApi.Mods;
    using UnityEngine;

    /// <summary>
    /// A singleton object representing this mod that is instantiated and initialized
    /// when the mod is loaded.
    /// </summary>
    public class Mod : GameMod
    {
        private GameObject _serviceObject;
        private HarmonyLib.Harmony _harmony;

        private Mod()
            : base()
        {
        }

        /// <summary>
        /// Gets the singleton instance of the mod object.
        /// </summary>
        public static Mod Instance { get; } = GetModInstance<Mod>();

        /// <inheritdoc />
        protected override void OnModInitialized()
        {
            base.OnModInitialized();

            if (_serviceObject != null)
            {
                return;
            }

            _serviceObject = new GameObject("PlusOne Service");
            Object.DontDestroyOnLoad(_serviceObject);
            _serviceObject.AddComponent<PlusOneService>();

            // A real report: on an older game version (1.4.105.0c vs. the
            // 1.4.200.0c this mod was built against), MfdScript.IsScreenVisible
            // didn't exist yet, so Harmony's PatchAll threw
            // "Undefined target method" - an unhandled exception here aborts
            // OnModInitialized entirely, taking down the whole mod (server,
            // flight panel integration, everything) over what's really just
            // an MFD-flicker optimization (see MfdScreenVisibilityPatch).
            // Isolating this means a patch failure - from a game update, a
            // conflicting mod, or anything else - degrades gracefully
            // instead of preventing the mod from working at all.
            try
            {
                _harmony = new HarmonyLib.Harmony("com.dooiereier.plusone");
                _harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
            }
            catch (System.Exception ex)
            {
                Log.Warn($"Harmony patching failed, continuing without it: {ex.Message}");
            }

            FlightInfoPanelIntegration.Register();

            new ModUpdater().CheckForUpdate();

            Log.Info("PlusOne mod initialized.");
        }
    }
}
