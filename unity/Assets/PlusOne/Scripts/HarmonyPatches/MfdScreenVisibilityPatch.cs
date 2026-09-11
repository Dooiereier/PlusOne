namespace PlusOne.HarmonyPatches
{
    using Assets.Scripts.Craft.Parts.Modifiers.Mfd;
    using HarmonyLib;
    using PlusOne.Flight;

    /// <summary>
    /// Keeps an MFD's screen GameObject continuously active while the
    /// console's MFD tab is watching it, overriding the game's own decision
    /// (MfdScript.IsScreenVisible) to put the screen to sleep once the
    /// player's own camera is far enough away, for performance.
    /// </summary>
    /// <remarks>
    /// The first version of this feature fought the resulting SetActive(false)
    /// reactively (forcing it back on every captured frame), which visibly
    /// flickered: each re-activation re-triggers OnEnable on the screen's
    /// widgets, briefly resetting their content before it repopulated. Patching
    /// the decision itself instead means the game's own logic simply never
    /// disables the screen in the first place while we're watching it - no
    /// toggling, no OnEnable/OnDisable churn.
    /// </remarks>
    [HarmonyPatch(typeof(MfdScript), "IsScreenVisible")]
    internal static class MfdScreenVisibilityPatch
    {
        static void Postfix(MfdScript __instance, ref bool __result)
        {
            if (!__result && __instance.Canvas == MfdVisibilityOverride.ActiveCanvas)
            {
                __result = true;
            }
        }
    }
}
