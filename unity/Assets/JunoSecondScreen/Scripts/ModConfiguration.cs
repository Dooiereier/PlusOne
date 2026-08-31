namespace JunoSecondScreen
{
    using System;
    using UnityEngine;

    /// <summary>
    /// An immutable snapshot of the mod settings. The service compares snapshots so
    /// that changing a setting in game restarts the server without a game restart.
    /// </summary>
    internal readonly struct ModConfiguration : IEquatable<ModConfiguration>
    {
        private ModConfiguration(
            bool enabled,
            int port,
            bool requireToken,
            bool allowControl,
            int telemetryHz,
            bool videoEnabled,
            int videoWidth,
            int videoFps,
            int videoQuality)
        {
            Enabled = enabled;
            Port = port;
            RequireToken = requireToken;
            AllowControl = allowControl;
            TelemetryHz = telemetryHz;
            VideoEnabled = videoEnabled;
            VideoWidth = videoWidth;
            VideoFps = videoFps;
            VideoQuality = videoQuality;
        }

        public bool Enabled { get; }

        public int Port { get; }

        public bool RequireToken { get; }

        public bool AllowControl { get; }

        public int TelemetryHz { get; }

        public bool VideoEnabled { get; }

        public int VideoWidth { get; }

        public int VideoFps { get; }

        public int VideoQuality { get; }

        /// <summary>
        /// Reads the current values from the in game settings panel.
        /// </summary>
        /// <param name="configuration">The settings snapshot.</param>
        /// <returns>
        /// <c>false</c> while Juno has not finished registering the mod's settings
        /// category, in which case the caller should try again shortly.
        /// </returns>
        public static bool TryRead(out ModConfiguration configuration)
        {
            try
            {
                configuration = FromSettings();
                return true;
            }
            catch (Exception)
            {
                configuration = default;
                return false;
            }
        }

        private static ModConfiguration FromSettings()
        {
            ModSettings settings = ModSettings.Instance;
            return new ModConfiguration(
                settings.Enabled.Value,
                Mathf.RoundToInt(settings.Port.Value),
                settings.RequireToken.Value,
                settings.AllowControl.Value,
                Mathf.RoundToInt(settings.TelemetryRate.Value),
                settings.VideoEnabled.Value,
                Mathf.RoundToInt(settings.VideoWidth.Value),
                Mathf.RoundToInt(settings.VideoFps.Value),
                Mathf.RoundToInt(settings.VideoQuality.Value));
        }

        public bool Equals(ModConfiguration other)
        {
            return Enabled == other.Enabled
                && Port == other.Port
                && RequireToken == other.RequireToken
                && AllowControl == other.AllowControl
                && TelemetryHz == other.TelemetryHz
                && VideoEnabled == other.VideoEnabled
                && VideoWidth == other.VideoWidth
                && VideoFps == other.VideoFps
                && VideoQuality == other.VideoQuality;
        }

        public override bool Equals(object obj)
        {
            return obj is ModConfiguration other && Equals(other);
        }

        public override int GetHashCode()
        {
            int hash = Enabled ? 17 : 19;
            hash = (hash * 31) + Port;
            hash = (hash * 31) + (RequireToken ? 1 : 0);
            hash = (hash * 31) + (AllowControl ? 1 : 0);
            hash = (hash * 31) + TelemetryHz;
            hash = (hash * 31) + (VideoEnabled ? 1 : 0);
            hash = (hash * 31) + VideoWidth;
            hash = (hash * 31) + VideoFps;
            hash = (hash * 31) + VideoQuality;
            return hash;
        }
    }
}
