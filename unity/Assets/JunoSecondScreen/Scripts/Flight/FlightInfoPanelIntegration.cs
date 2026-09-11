namespace JunoSecondScreen.Flight
{
    using System;
    using JunoSecondScreen.Util;
    using ModApi.Common;
    using ModApi.Ui;
    using ModApi.Ui.Inspector;
    using UnityEngine;

    /// <summary>
    /// Adds a "PlusOne" section directly into the game's own Flight Info
    /// panel, the same way built-in sections like "Fuel" and "Velocity" are
    /// built (InspectorModel/GroupModel).
    ///
    /// This used to be a Harmony postfix on GameViewInspectorScript.Start that
    /// grabbed the panel's model via reflection and appended a group to it
    /// after Start() returned - which worked for the stock panel display, but
    /// runs too late for mods like Flight Info Plus that build their own
    /// summary UI by reading the model's Groups list once, during panel
    /// construction (via Game.Instance.UserInterface.AddBuildInspectorPanelAction,
    /// the officially documented extension point for "modify the inspector
    /// panel model prior to the inspector panel being created" - see
    /// ModApi.Ui.IUserInterface.AddBuildInspectorPanelAction's own doc
    /// comment). By the time our old postfix ran, that snapshot had already
    /// been taken, so our group simply never existed as far as Flight Info
    /// Plus's build pass was concerned. Registering through the same official
    /// hook FI+ itself uses puts us in the same build pass instead of after it.
    /// </summary>
    internal static class FlightInfoPanelIntegration
    {
        public static void Register()
        {
            try
            {
                Game.Instance.UserInterface.AddBuildInspectorPanelAction(InspectorIds.FlightView, OnBuildFlightViewPanel);
            }
            catch (Exception ex)
            {
                Log.Warn("FlightInfoPanelIntegration: could not register the panel build action: " + ex.Message);
            }
        }

        private static void OnBuildFlightViewPanel(BuildInspectorPanelRequest request)
        {
            try
            {
                request.Model.AddGroup(BuildGroup());
            }
            catch (Exception ex)
            {
                Log.Warn("FlightInfoPanelIntegration: failed to add the PlusOne group: " + ex.Message);
            }
        }

        private static GroupModel BuildGroup()
        {
            var group = new GroupModel("PlusOne", null) { Collapsed = true };

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
