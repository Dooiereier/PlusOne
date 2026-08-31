namespace JunoSecondScreen
{
    using System.Globalization;
    using ModApi.Common;
    using ModApi.Settings.Core;

    /// <summary>
    /// The mod's page in Juno's settings screen.
    /// </summary>
    /// <seealso cref="ModApi.Settings.Core.SettingsCategory{JunoSecondScreen.ModSettings}" />
    public class ModSettings : SettingsCategory<ModSettings>
    {
        private static ModSettings _instance;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModSettings"/> class.
        /// </summary>
        public ModSettings()
            : base("Second Screen")
        {
        }

        /// <summary>
        /// Gets the mod settings instance.
        /// </summary>
        public static ModSettings Instance =>
            _instance ?? (_instance = Game.Instance.Settings.ModSettings.GetCategory<ModSettings>());

        /// <summary>
        /// Gets a value indicating whether the console server runs at all.
        /// </summary>
        public BoolSetting Enabled { get; private set; }

        /// <summary>
        /// Gets the TCP port the console is served on.
        /// </summary>
        public NumericSetting<float> Port { get; private set; }

        /// <summary>
        /// Gets a value indicating whether clients must present the access token.
        /// </summary>
        public BoolSetting RequireToken { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the tablet may send control input.
        /// </summary>
        public BoolSetting AllowControl { get; private set; }

        /// <summary>
        /// Gets how many telemetry frames per second are pushed to the tablet.
        /// </summary>
        public NumericSetting<float> TelemetryRate { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the video feed endpoint is available.
        /// </summary>
        public BoolSetting VideoEnabled { get; private set; }

        /// <summary>
        /// Gets the width, in pixels, that video frames are scaled down to.
        /// </summary>
        public NumericSetting<float> VideoWidth { get; private set; }

        /// <summary>
        /// Gets the video feed's frame rate.
        /// </summary>
        public NumericSetting<float> VideoFps { get; private set; }

        /// <summary>
        /// Gets the JPEG quality of the video feed.
        /// </summary>
        public NumericSetting<float> VideoQuality { get; private set; }

        /// <inheritdoc />
        protected override void InitializeSettings()
        {
            this.Enabled = this.CreateBool("Enabled")
                .SetDescription("Serve the tablet console over your local network. The address is written to the log and shown when a flight starts.")
                .SetDefault(true);

            this.Port = this.CreateNumeric<float>("Port", 1024f, 65535f, 1f)
                .SetDescription("The TCP port the console is served on. Change it if another program already uses this port.")
                .SetDisplayFormatter(FormatInteger)
                .SetDefault(8088f);

            this.RequireToken = this.CreateBool("Require access token")
                .SetDescription("Only allow devices that open the address including the ?t=... token. Turn this off on a network you trust.")
                .SetDefault(true);

            this.AllowControl = this.CreateBool("Allow control input")
                .SetDescription("Let the tablet stage, set throttle, toggle activation groups and change time warp. Turn off for a read only console.")
                .SetDefault(true);

            this.TelemetryRate = this.CreateNumeric<float>("Telemetry rate", 5f, 30f, 1f)
                .SetDescription("Telemetry frames sent per second. Higher is smoother, lower is lighter on the network.")
                .SetDisplayFormatter(value => FormatInteger(value) + " Hz")
                .SetDefault(15f);

            this.VideoEnabled = this.CreateBool("Enable video feed")
                .SetDescription("Allow the console's View tab to stream the game window. The feed only costs performance while a tablet is watching it.")
                .SetDefault(true);

            this.VideoWidth = this.CreateNumeric<float>("Video width", 320f, 1280f, 32f)
                .SetDescription("Width in pixels that video frames are scaled down to before being sent.")
                .SetDisplayFormatter(value => FormatInteger(value) + " px")
                .SetDefault(640f);

            this.VideoFps = this.CreateNumeric<float>("Video frame rate", 1f, 30f, 1f)
                .SetDescription("Frames per second for the video feed.")
                .SetDisplayFormatter(value => FormatInteger(value) + " fps")
                .SetDefault(12f);

            this.VideoQuality = this.CreateNumeric<float>("Video quality", 30f, 90f, 5f)
                .SetDescription("JPEG quality of the video feed. Lower values use less bandwidth.")
                .SetDisplayFormatter(FormatInteger)
                .SetDefault(60f);
        }

        private static string FormatInteger(float value)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture);
        }
    }
}
