namespace JunoSecondScreen.Flight
{
    using JunoSecondScreen.Util;
    using ModApi.Craft;
    using ModApi.Planet;
    using UnityEngine;

    /// <summary>
    /// Holds an encoded PNG of the current planet's equirectangular color map
    /// for the console's orbit tab, generated via the game's own
    /// PlanetCubemapUtility.CreateEquirectangularMap - the same engine code
    /// that projects a planet's 6-face color cubemap into a flat map, rather
    /// than a hand-rolled cubemap-to-equirect reimplementation (an earlier
    /// version of this class did that, and it produced a visibly distorted,
    /// seamed result on the first live test - this replaces it with the
    /// real, tested projection instead of continuing to debug a
    /// reimplementation of it).
    ///
    /// Resolving and generating a planet's map involves Unity texture APIs,
    /// which only work on the main thread, while the map is served from a
    /// background per-connection thread - so this is refreshed from Update()
    /// (main thread, at a slow, deliberate cadence) and read from any thread
    /// via a lock around a single, always-complete byte[] reference.
    /// </summary>
    internal sealed class PlanetMapCache
    {
        private const int TargetWidth = 1024;
        private const int TargetHeight = TargetWidth / 2; // equirectangular is always 2:1

        private readonly object _lock = new object();
        private byte[] _png;
        private string _cachedPlanetName;

        /// <summary>
        /// The planet this cache's current PNG is for, or null if nothing has
        /// been resolved yet. Read/written only from the main thread.
        /// </summary>
        public string PlanetName { get; private set; }

        /// <summary>
        /// The planet's own RotationAngle (degrees) at the moment the current
        /// PNG was generated. The map is a static image generated once per
        /// planet and then cached indefinitely, but the planet keeps
        /// spinning underneath it - if a live position marker is plotted
        /// straight onto the map using only its current body-fixed lat/lon,
        /// it visibly drifts as real rotation continues past the instant the
        /// map was generated. The client compensates for that by shifting
        /// the marker's longitude by the difference between this value and
        /// the planet's current live rotation angle (also sent every
        /// telemetry frame), so it stays aligned with the frozen map instead
        /// of the live planet. Read/written only from the main thread.
        /// </summary>
        public float MapRotationAngleDeg { get; private set; }

        /// <summary>
        /// Re-resolves the current planet and, if it has changed since the
        /// last call, regenerates its map. Cheap when nothing changed (a
        /// string comparison). Main thread only.
        /// </summary>
        public void Refresh(ICraftScript craft)
        {
            var planetNode = craft?.FlightData?.Orbit?.Parent;
            IPlanetData planetData = planetNode?.PlanetData;
            PlanetName = planetData?.Name;

            if (planetData == null || planetData.Name == _cachedPlanetName)
            {
                return;
            }

            byte[] png = Generate(planetData);
            if (png == null)
            {
                return;
            }

            lock (_lock)
            {
                _png = png;
                _cachedPlanetName = planetData.Name;
                MapRotationAngleDeg = (float)(planetNode.RotationAngle * Mathf.Rad2Deg);
            }
        }

        /// <summary>
        /// The most recently cached PNG bytes, or null if none has been
        /// resolved yet. Safe to call from any thread.
        /// </summary>
        public byte[] GetPng()
        {
            lock (_lock)
            {
                return _png;
            }
        }

        private static byte[] Generate(IPlanetData planetData)
        {
            // downsampleIterations=0 (we pick our own target size directly),
            // brightnessAdjustment/lighting=0 (raw base color, no artificial
            // relight), saveMaps=false (return textures in memory, don't
            // write them to disk under some engine-chosen path).
            Texture2D[] maps = PlanetCubemapUtility.CreateEquirectangularMap(
                planetData, TargetWidth, TargetHeight, 0, 0f, 0f, false);

            if (maps == null || maps.Length == 0 || maps[0] == null)
            {
                Log.Warn($"PlanetMapCache: CreateEquirectangularMap returned nothing for '{planetData.Name}'.");
                return null;
            }

            // Deliberately not Destroy()-ing maps[] here: it's undocumented
            // whether these textures are freshly allocated for this call or
            // references into something the engine keeps around internally,
            // and destroying the latter could break unrelated planet
            // rendering. Worst case this leaks one small texture per planet
            // switch, which is rare and not worth that risk.
            return maps[0].EncodeToPNG();
        }
    }
}
