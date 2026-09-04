namespace JunoSecondScreen.Flight
{
    using System.Collections.Generic;
    using Assets.Scripts.Craft.Parts.Modifiers;
    using ModApi.Craft;

    /// <summary>
    /// Finds the craft's camera-vantage parts (nose cam, docking cam, etc.) -
    /// the same part-modifier mechanism the game's own camera system uses for
    /// its built-in view switching. Must only be called from the main thread.
    /// </summary>
    internal sealed class CameraVantageCollector
    {
        /// <summary>
        /// Lists the part names of every camera-vantage part on the craft.
        /// </summary>
        public List<string> ListNames(ICraftScript craft)
        {
            var names = new List<string>();
            var parts = craft?.Data?.Assembly?.Parts;
            if (parts == null)
            {
                return names;
            }

            foreach (var part in parts)
            {
                if (part.GetModifier<CameraVantageData>()?.Script?.CameraPosition != null)
                {
                    names.Add(part.Name);
                }
            }

            return names;
        }

        /// <summary>
        /// Finds the camera-vantage part with the given name, or null if no
        /// such part (or no camera vantage on it) exists on the craft.
        /// </summary>
        public CameraVantageScript Find(string partName, ICraftScript craft)
        {
            var parts = craft?.Data?.Assembly?.Parts;
            if (parts == null)
            {
                return null;
            }

            foreach (var part in parts)
            {
                if (part.Name != partName)
                {
                    continue;
                }

                return part.GetModifier<CameraVantageData>()?.Script;
            }

            return null;
        }
    }
}
