namespace JunoSecondScreen
{
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

            _serviceObject = new GameObject("Second Screen Service");
            Object.DontDestroyOnLoad(_serviceObject);
            _serviceObject.AddComponent<SecondScreenService>();

            Log.Info("Second Screen mod initialized.");
        }
    }
}
