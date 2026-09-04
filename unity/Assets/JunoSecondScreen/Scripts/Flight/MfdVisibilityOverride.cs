namespace JunoSecondScreen.Flight
{
    using UnityEngine;

    /// <summary>
    /// Tracks which MFD Canvas (if any) the console's MFD tab currently has a
    /// subscriber watching, so MfdScreenVisibilityPatch can keep that specific
    /// MFD's screen from being put to sleep while someone's actually watching
    /// it. Must only be read/written from the Unity main thread.
    /// </summary>
    internal static class MfdVisibilityOverride
    {
        public static Canvas ActiveCanvas;
    }
}
