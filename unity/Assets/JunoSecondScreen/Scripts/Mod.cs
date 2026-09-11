namespace JunoSecondScreen
{
    using JunoSecondScreen.Flight;
    using JunoSecondScreen.Util;
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
            _serviceObject.AddComponent<SecondScreenService>();

            _harmony = new HarmonyLib.Harmony("com.dooiereier.junosecondscreen");
            _harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());

            FlightInfoPanelIntegration.Register();

            new ModUpdater().CheckForUpdate();

            Log.Info("PlusOne mod initialized.");
        }
    }
}
