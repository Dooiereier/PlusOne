namespace JunoSecondScreen.Flight
{
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using JunoSecondScreen.Util;
    using ModApi.Common;
    using ModApi.Craft;
    using ModApi.Flight.UI;
    using UnityEngine;

    /// <summary>
    /// Queues control commands from the tablet and applies them to the active craft
    /// on the Unity main thread.
    /// </summary>
    /// <remarks>
    /// Held axes (throttle, brake) are re-applied for a short window after the last
    /// message so that a value survives the frames in which Juno recomputes controls
    /// from local input, then released again so the keyboard on the PC keeps working.
    /// </remarks>
    internal sealed class CommandProcessor
    {
        private const float HoldSeconds = 0.35f;
        private const int MaxQueuedCommands = 64;

        private readonly ConcurrentQueue<Dictionary<string, object>> _queue = new ConcurrentQueue<Dictionary<string, object>>();

        private float _throttle;
        private float _throttleHoldUntil = float.NegativeInfinity;
        private float _brake;
        private float _brakeHoldUntil = float.NegativeInfinity;

        /// <summary>
        /// Gets or sets a value indicating whether commands are accepted at all.
        /// </summary>
        public bool ControlEnabled { get; set; } = true;

        /// <summary>
        /// Parses and queues a command message. Called from a network thread.
        /// </summary>
        /// <param name="json">The raw message from the console.</param>
        public void Enqueue(string json)
        {
            if (!ControlEnabled || _queue.Count >= MaxQueuedCommands)
            {
                return;
            }

            if (JsonReader.Parse(json) is Dictionary<string, object> command && command.ContainsKey("cmd"))
            {
                _queue.Enqueue(command);
            }
        }

        /// <summary>
        /// Applies every queued command and any still-held axis. Main thread only.
        /// </summary>
        public void Apply()
        {
            ICraftScript craft = Game.Instance.FlightScene?.CraftNode?.CraftScript;

            while (_queue.TryDequeue(out Dictionary<string, object> command))
            {
                if (ControlEnabled)
                {
                    Execute(command, craft);
                }
            }

            ApplyHeldAxes(craft);
        }

        /// <summary>
        /// Drops any pending commands and releases held axes, so a disconnecting
        /// tablet cannot leave the craft with a stuck input.
        /// </summary>
        public void Reset()
        {
            while (_queue.TryDequeue(out _))
            {
            }

            _throttleHoldUntil = float.NegativeInfinity;
            _brakeHoldUntil = float.NegativeInfinity;
            _brake = 0f;
        }

        private void Execute(Dictionary<string, object> command, ICraftScript craft)
        {
            string name = JsonReader.GetString(command, "cmd");
            var pod = craft?.ActiveCommandPod;

            switch (name)
            {
                case "throttle":
                    _throttle = Mathf.Clamp01((float)JsonReader.GetDouble(command, "v"));
                    _throttleHoldUntil = Time.unscaledTime + HoldSeconds;
                    break;

                case "brake":
                    _brake = Mathf.Clamp01((float)JsonReader.GetDouble(command, "v"));
                    _brakeHoldUntil = Time.unscaledTime + HoldSeconds;
                    break;

                case "stage":
                    pod?.ActivateStage();
                    break;

                case "ag":
                    int group = JsonReader.GetInt(command, "i");
                    if (pod != null && group >= 1)
                    {
                        pod.SetActivationGroupState(group, JsonReader.GetBool(command, "on"));
                    }

                    break;

                case "translation":
                    pod?.Controls?.ToggleTranslationMode();
                    break;

                case "warp":
                    ChangeWarp(JsonReader.GetInt(command, "d"));
                    break;

                case "pause":
                    TogglePause();
                    break;

                case "lock":
                    SetHeadingLock(JsonReader.GetString(command, "mode"));
                    break;
            }
        }

        private void ApplyHeldAxes(ICraftScript craft)
        {
            var controls = craft?.ActiveCommandPod?.Controls;
            if (controls == null)
            {
                return;
            }

            if (Time.unscaledTime <= _throttleHoldUntil)
            {
                controls.Throttle = _throttle;
            }

            if (Time.unscaledTime <= _brakeHoldUntil)
            {
                controls.Brake = _brake;
                controls.OffsetBrake = _brake;
            }
        }

        private static void ChangeWarp(int direction)
        {
            var timeManager = Game.Instance.FlightScene?.TimeManager;
            if (timeManager == null || direction == 0)
            {
                return;
            }

            if (direction > 0)
            {
                timeManager.IncreaseTimeMultiplier();
            }
            else
            {
                timeManager.DecreaseTimeMultiplier();
            }
        }

        private static void TogglePause()
        {
            var timeManager = Game.Instance.FlightScene?.TimeManager;
            timeManager?.RequestPauseChange(!timeManager.Paused, true);
        }

        private static void SetHeadingLock(string mode)
        {
            INavSphere navSphere = Game.Instance.FlightScene?.FlightSceneUI?.NavSphere;
            if (navSphere == null || string.IsNullOrEmpty(mode))
            {
                return;
            }

            navSphere.UnlockHeading();
            switch (mode)
            {
                case "prograde":
                    navSphere.ToggleLock(NavSphereIndicatorType.VelocityPrograde);
                    break;
                case "retrograde":
                    navSphere.ToggleLock(NavSphereIndicatorType.VelocityRetrograde);
                    break;
                case "target":
                    navSphere.ToggleLock(NavSphereIndicatorType.Target);
                    break;
                case "node":
                    navSphere.ToggleLock(NavSphereIndicatorType.ManeuverNode);
                    break;
                case "none":
                    break;
            }
        }
    }
}
