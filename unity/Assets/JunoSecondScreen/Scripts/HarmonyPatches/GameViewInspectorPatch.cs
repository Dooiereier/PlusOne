namespace JunoSecondScreen.HarmonyPatches
{
    using System;
    using Assets.Scripts.Flight.GameView.UI.Inspector;
    using HarmonyLib;
    using JunoSecondScreen.Util;
    using ModApi.Ui.Inspector;
    using UnityEngine;

    /// <summary>
    /// Adds a "Second Screen" section directly into the game's own Flight Info
    /// panel (GameViewInspectorScript), the same way built-in sections like
    /// "Fuel" and "Velocity" are built (InspectorModel/GroupModel), instead of
    /// opening a separate floating panel or adding a new toolbar button.
    /// </summary>
    [HarmonyPatch(typeof(GameViewInspectorScript), "Start")]
    internal static class GameViewInspectorPatch
    {
        static void Postfix(GameViewInspectorScript __instance)
        {
            try
            {
                var panel = Traverse.Create(__instance).Field("_inspectorPanel").GetValue<IInspectorPanel>();
                if (panel?.Model == null)
                {
                    Log.Warn("GameViewInspectorPatch: could not find the inspector panel/model.");
                    return;
                }

                panel.Model.AddGroup(BuildGroup());
                panel.RebuildModelElements();
            }
            catch (Exception ex)
            {
                Log.Warn("GameViewInspectorPatch failed: " + ex.Message);
            }
        }

        private static GroupModel BuildGroup()
        {
            var group = new GroupModel("Second Screen", null) { Collapsed = true };

            group.Add(new ToggleModel(
                "Enabled",
                () => SecondScreenService.Instance != null && SecondScreenService.Instance.IsRunning,
                enabled => SecondScreenService.Instance?.ToggleEnabled(),
                null));

            group.Add(new TextModel(" ", () => string.Empty, null, null, null));

            // A plain full-width label instead of a "Address"/value TextModel
            // row: TextModel lays label and value out in two fixed columns on
            // the same line, and the connection URL is long enough to overflow
            // its column and overlap the "Address" label. LabelModel has no
            // separate label column, so the whole row is free for the value.
            var addressLabel = new LabelModel("Not running", ElementAlignment.Left);
            addressLabel.UpdateAction = _ =>
            {
                addressLabel.Label = SecondScreenService.Instance?.ConnectionAddress ?? "Not running";
            };
            group.Add(addressLabel);

            group.Add(new TextModel(" ", () => string.Empty, null, null, null));

            group.Add(new TextButtonModel(
                "Copy Address",
                _ =>
                {
                    var address = SecondScreenService.Instance?.ConnectionAddress;
                    if (!string.IsNullOrEmpty(address))
                    {
                        GUIUtility.systemCopyBuffer = address;
                    }
                },
                null, null));

            return group;
        }
    }
}
