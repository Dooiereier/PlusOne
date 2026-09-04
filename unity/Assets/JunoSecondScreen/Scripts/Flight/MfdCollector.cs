namespace JunoSecondScreen.Flight
{
    using Assets.Scripts.Craft.Parts.Modifiers.Mfd; // internal namespace for MfdData/MfdScript
    using JunoSecondScreen.Util;
    using ModApi.Craft;
    using TMPro;
    using UnityEngine;
    using UnityEngine.UI;

    /// <summary>
    /// Reads MFD parts' widget trees by walking their Unity Canvas hierarchy,
    /// and serializes them into JSON for the tablet console.
    /// Must only be called from the Unity main thread.
    /// </summary>
    internal sealed class MfdCollector
    {
        private readonly JsonWriter _json = new JsonWriter(8192);
        private readonly CameraVantageCollector _cameraCollector = new CameraVantageCollector();

        /// <summary>
        /// Finds the Canvas of the MFD part with the given name, or null if
        /// no such part (or no MFD on it) exists on the craft. Used by the
        /// MFD camera stream to point its capture camera at the right part.
        /// </summary>
        public static Canvas FindCanvas(string partName, ICraftScript craft)
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

                return part.GetModifier<MfdData>()?.Script?.Canvas;
            }

            return null;
        }

        /// <summary>
        /// Builds one MFD frame covering every MFD part on the given craft.
        /// Returns a "no craft"/"no mfd" document (not null) so the caller
        /// can always publish something, mirroring TelemetryCollector.Build().
        /// </summary>
        /// <param name="mfdFrameVersion">
        /// The MFD tab's current video-frame counter, so the console can tell
        /// a genuinely stalled feed (this stops moving) apart from an idle one
        /// (nothing selected/watched).
        /// </param>
        public string Build(ICraftScript craft, int mfdFrameVersion, int mainViewFrameVersion, int externalViewFrameVersion)
        {
            _json.Reset();
            _json.StartObject();
            _json.Prop("type", "mfd");
            _json.Prop("mfdFrameVersion", mfdFrameVersion);
            _json.Prop("mainViewFrameVersion", mainViewFrameVersion);
            _json.Prop("externalViewFrameVersion", externalViewFrameVersion);

            _json.StartArray("mfds");
            var parts = craft?.Data?.Assembly?.Parts;
            if (parts != null)
            {
                foreach (var part in parts)
                {
                    var mfdData = part.GetModifier<MfdData>();
                    if (mfdData?.Script?.Canvas == null)
                    {
                        continue;
                    }

                    WriteMfd(part.Name, mfdData.Script.Canvas.transform);
                }
            }

            _json.EndArray();

            _json.StartArray("cameras");
            foreach (string name in _cameraCollector.ListNames(craft))
            {
                _json.Value(name);
            }

            _json.EndArray();

            _json.EndObject();
            return _json.ToString();
        }

        private void WriteMfd(string partName, Transform canvasTransform)
        {
            _json.StartObject();
            _json.Prop("part", partName);
            _json.StartArray("widgets");
            WriteChildren(canvasTransform);
            _json.EndArray();
            _json.EndObject();
        }

        // Writes every direct child of 'parent' as a widget, recursing into
        // each one so nested/parented widgets end up nested in the JSON too.
        private void WriteChildren(Transform parent)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                WriteWidget(parent.GetChild(i));
            }
        }

        private void WriteWidget(Transform widget)
        {
            _json.StartObject();

            _json.Prop("name", widget.name);
            _json.Prop("visible", widget.gameObject.activeSelf);

            if (widget is RectTransform rect)
            {
                WriteVector2("position", rect.anchoredPosition);
                WriteVector2("pivot", rect.pivot);
                WriteVector2("size", rect.sizeDelta);
                WriteVector2("scale", rect.localScale);
                _json.Prop("rotation", rect.localEulerAngles.z);
            }

            // Text label
            var label = widget.GetComponent<TextMeshProUGUI>();
            if (label != null)
            {
                _json.Prop("text", label.text);
                _json.Prop("fontSize", label.fontSize);
                WriteColor("color", label.color);
            }

            // Gauge / sprite fill (Unity UI Image with fillAmount)
            var image = widget.GetComponent<Image>();
            if (image != null)
            {
                _json.Prop("fillAmount", image.fillAmount);
                _json.Prop("fillMethod", image.fillMethod.ToString());
                WriteColor("fillColor", image.color);
            }

            // Line
            var line = widget.GetComponent<LineRenderer>();
            if (line != null)
            {
                _json.Prop("thickness", line.startWidth);
            }

            _json.StartArray("children");
            WriteChildren(widget);
            _json.EndArray();

            _json.EndObject();
        }

        private void WriteVector2(string key, Vector2 value)
        {
            _json.StartObject(key);
            _json.Prop("x", value.x);
            _json.Prop("y", value.y);
            _json.EndObject();
        }

        private void WriteColor(string key, Color color)
        {
            _json.PropVector(key, color.r, color.g, color.b);
        }
    }
}
