namespace JunoSecondScreen.Flight
{
    using System.Collections.Generic;
    using JunoSecondScreen.Util;
    using ModApi.Common;
    using ModApi.Craft;
    using ModApi.Craft.Parts;
    using ModApi.Flight.Sim;
    using ModApi.Planet;
    using UnityEngine;

    /// <summary>
    /// Reads the active craft's state and serializes it into the JSON frame the
    /// tablet console consumes. Must only be called from the Unity main thread.
    /// </summary>
    internal sealed class TelemetryCollector
    {
        /// <summary>
        /// Activation group names change rarely, and reading them allocates, so the
        /// list is refreshed on a slow cadence instead of every frame.
        /// </summary>
        private const float GroupNameRefreshSeconds = 1f;

        /// <summary>
        /// Juno exposes ten activation groups on the flight HUD.
        /// </summary>
        private const int ActivationGroupCount = 10;

        private readonly JsonWriter _json = new JsonWriter(8192);
        private readonly List<string> _groupNames = new List<string>();
        private float _groupNamesRefreshedAt = float.NegativeInfinity;
        private float _nextTrajectoryDiagAt = float.NegativeInfinity;
        private float _nextLongitudeDiagAt = float.NegativeInfinity;

        // How often the diagnostic logs below may repeat - once-ever made a
        // diagnostic useless for anything after the first log of a flight
        // (an already-fired diagnostic stayed silent for the rest of the
        // session, including exactly the later, in-orbit state that needed
        // checking). A slow repeat keeps them useful without spamming.
        private const float DiagRepeatSeconds = 15f;

        /// <summary>
        /// Builds one telemetry frame.
        /// </summary>
        /// <param name="mapRotationAngleDeg">
        /// The orbited planet's RotationAngle at the moment the cached
        /// planet map PNG was generated - see PlanetMapCache.MapRotationAngleDeg
        /// for why the client needs this alongside the live rotation angle
        /// sent below.
        /// </param>
        /// <param name="includeTrajectory">
        /// Whether to sample the ground-track trail at all. That sampling
        /// (up to ~540 IOrbitNode.GetPointAtTime calls, capped to twice a
        /// second - see WriteTrajectory) is only useful while a connected
        /// client actually has the Orbit tab open to look at it; every other
        /// property below is cheap enough to always send regardless of tab.
        /// </param>
        /// <returns>A JSON document describing the current flight.</returns>
        public string Build(float mapRotationAngleDeg, bool includeTrajectory)
        {
            _json.Reset();
            _json.StartObject();
            _json.Prop("type", "telemetry");

            var flightScene = Game.Instance.FlightScene;
            ICraftScript craft = flightScene?.CraftNode?.CraftScript;
            ICraftFlightData flightData = craft?.FlightData;

            if (flightData == null)
            {
                _json.Prop("inFlight", false);
                _json.EndObject();
                return _json.ToString();
            }

            _json.Prop("inFlight", true);
            WriteSceneState(flightScene, craft);
            WriteFlightState(flightData, mapRotationAngleDeg);
            WritePerformance(craft, flightData);
            WriteOrbit(craft, flightData, includeTrajectory);
            WriteAttitude(flightData);
            WriteControls(craft);

            _json.EndObject();
            return _json.ToString();
        }

        private void WriteSceneState(ModApi.Flight.IFlightScene flightScene, ICraftScript craft)
        {
            _json.Prop("craft", GetCraftName(craft?.CraftNode));

            var timeManager = flightScene?.TimeManager;
            _json.Prop("warp", timeManager?.CurrentMode != null ? timeManager.CurrentMode.TimeMultiplier : 1d);
            _json.Prop("paused", timeManager != null && timeManager.Paused);
            _json.Prop("met", flightScene?.FlightState != null ? flightScene.FlightState.Time : 0d);
        }

        private void WriteFlightState(ICraftFlightData flightData, float mapRotationAngleDeg)
        {
            var planetNode = flightData.Orbit?.Parent;
            var planetData = planetNode?.PlanetData;
            var atmosphere = flightData.AtmosphereSample;

            _json.Prop("planet", planetData != null ? planetData.Name : "—");
            _json.Prop("planetRadius", planetData != null ? planetData.Radius : 0d);
            _json.Prop("planetRotationAngle", planetNode != null ? planetNode.RotationAngle * Mathf.Rad2Deg : 0d);
            _json.Prop("mapRotationAngle", mapRotationAngleDeg);

            _json.Prop("altAsl", flightData.AltitudeAboveSeaLevel);
            _json.Prop("altAgl", flightData.AltitudeAboveGroundLevel);
            _json.Prop("surfaceSpeed", flightData.SurfaceVelocityMagnitude);
            _json.Prop("orbitalSpeed", flightData.VelocityMagnitude);
            _json.Prop("verticalSpeed", flightData.VerticalSurfaceVelocity);
            _json.Prop("horizontalSpeed", flightData.LateralSurfaceVelocity);
            _json.Prop("mach", flightData.MachNumber);
            _json.Prop("radius", flightData.Position.magnitude);

            double gravity = flightData.GravityMagnitude;
            _json.Prop("gForce", gravity > 1e-6 ? flightData.AccelerationMagnitude / gravity : 0d);

            _json.Prop("airPressure", atmosphere.AirPressure);
            _json.Prop("airDensity", atmosphere.AirDensity);
            _json.Prop("atmosphereHeight", atmosphere.AtmosphereHeight);

            if (planetNode != null)
            {
                // GetSurfaceCoordinates' own doc comment calls its input a
                // "surface position" - i.e. body-fixed/rotating-frame - but
                // PositionNormalized (like Position, used for the orbital
                // element derivation above) is in the inertial frame, not
                // rotating with the planet. Fed an inertial direction, the
                // function has no way to know the planet has turned since
                // its own rotation=0 reference, so its output is really an
                // inertial longitude. Confirmed live with exact numbers: a
                // stationary Florida craft (true longitude about -80) had
                // rawLon=129.5, rotationAngle=-210.2, and 129.5 + (-210.2)
                // = -80.7 - a near-exact match, and the sign that actually
                // fixes it (an earlier version subtracted rotationAngle
                // instead, which was backwards and left a ~60 degree gap).
                planetNode.GetSurfaceCoordinates(flightData.PositionNormalized, out double latitude, out double rawLongitude);
                double rotationDeg = planetNode.RotationAngle * Mathf.Rad2Deg;
                double longitudeDeg = rawLongitude * Mathf.Rad2Deg + rotationDeg;
                longitudeDeg = ((longitudeDeg + 180d) % 360d + 360d) % 360d - 180d;

                _json.Prop("latitude", latitude * Mathf.Rad2Deg);
                _json.Prop("longitude", longitudeDeg);

                if (Time.unscaledTime >= _nextLongitudeDiagAt)
                {
                    _nextLongitudeDiagAt = Time.unscaledTime + DiagRepeatSeconds;
                    Log.Info(
                        $"[Vizzy longitude diag] rawLon={rawLongitude * Mathf.Rad2Deg:F1} rotationAngle={rotationDeg:F1} " +
                        $"correctedLon={longitudeDeg:F1}");
                }
            }
            else
            {
                _json.Prop("latitude", 0d);
                _json.Prop("longitude", 0d);
            }

            _json.Prop("fuel", flightData.RemainingFuelInStage);
            _json.Prop("monoprop", flightData.RemainingMonopropellant);
            _json.Prop("battery", flightData.RemainingBattery);
            _json.Prop("mass", flightData.CurrentMassUnscaled);
        }

        private void WritePerformance(ICraftScript craft, ICraftFlightData flightData)
        {
            var performance = flightData.Performance;
            _json.Prop("twr", performance != null ? performance.ThrustToWeightRatio : 0d);
            _json.Prop("deltaV", performance != null ? performance.DeltaVStage : 0d);
            _json.Prop("isp", performance != null ? performance.CurrentIsp : 0d);
            _json.Prop("burnTime", performance != null ? performance.RemainingBurnTime : 0d);

            _json.Prop("thrust", flightData.CurrentEngineThrustUnscaled);
            _json.Prop("maxThrust", flightData.MaxActiveEngineThrustUnscaled);
            _json.Prop("activeEngines", Count(flightData.ActiveEngines));
            _json.Prop("activeRcs", Count(flightData.ActiveReactionControlNozzles));

            ICommandPod pod = craft.ActiveCommandPod;
            _json.Prop("stage", pod != null ? pod.CurrentStage : 0);
            _json.Prop("stages", pod != null ? pod.NumStages : 0);
            WriteActivationGroups(pod);
        }

        private void WriteActivationGroups(ICommandPod pod)
        {
            _json.StartArray("groups");
            if (pod != null)
            {
                RefreshGroupNames(pod);
                for (int group = 1; group <= ActivationGroupCount; group++)
                {
                    _json.StartObject();
                    _json.Prop("i", group);
                    _json.Prop("name", group <= _groupNames.Count ? _groupNames[group - 1] : string.Empty);
                    _json.Prop("on", pod.GetActivationGroupState(group));
                    _json.EndObject();
                }
            }

            _json.EndArray();
        }

        private void WriteOrbit(ICraftScript craft, ICraftFlightData flightData, bool includeTrajectory)
        {
            ICraftOrbitData orbit = flightData.Orbit;
            _json.Prop("apoapsis", orbit != null ? orbit.ApoapsisAltitude : 0d);
            _json.Prop("periapsis", orbit != null ? orbit.PeriapsisAltitude : 0d);
            _json.Prop("timeToAp", orbit != null ? orbit.ApoapsisTime : 0d);
            _json.Prop("timeToPe", orbit != null ? orbit.PeriapsisTime : 0d);
            _json.Prop("eccentricity", orbit != null ? orbit.Eccentricity : 0d);
            _json.Prop("inclination", orbit != null ? orbit.Inclination * Mathf.Rad2Deg : 0d);
            _json.Prop("period", orbit != null ? orbit.Period : 0d);

            if (includeTrajectory)
            {
                WriteTrajectory(craft, flightData, orbit);
            }
            else
            {
                _json.Prop("trajectoryAvailable", false);
            }
        }

        // The ground-track trail (past + future) is sampled via the game's
        // own orbit simulation (IOrbitNode.GetPointAtTime), not propagated
        // from hand-derived Keplerian elements. A from-scratch derivation
        // was tried twice (state vectors -> classical elements -> Kepler's
        // equation -> a perifocal-to-inertial rotation matrix, propagated
        // client-side) and each attempt passed its own static (dt=0)
        // verification but then failed live validation against
        // GetPointAtTime in a way neither attempt's author could fully
        // explain - a real, not-yet-understood bug, not a false alarm. This
        // version has no propagation math left to get wrong: the game
        // computes every point, and the only conversion left is the one
        // small, already-proven-correct step also used for the live marker
        // (an inertial position to body-fixed lat/lon via WriteFlightState's
        // own formula).
        // GetPointAtTime is not free - sampling 540+ points (more for an
        // escape trajectory) every single telemetry frame, at the default 15
        // Hz telemetry rate, was measured to cost noticeable FPS (a real
        // console client reported ~150 fps with the mod off vs. ~90-100 fps
        // with it on). None of that detail is visible to the player faster
        // than a fraction of a second anyway - a coasting orbit's ground
        // track barely moves within TrajectoryRecomputeSeconds - so the
        // expensive part is recomputed on its own, much slower clock and the
        // last result is reused (as a raw pre-built JSON fragment, spliced
        // into every frame via JsonWriter.AppendRaw) on the frames in
        // between, independent of the telemetry rate the rest of the frame
        // still uses.
        private const float TrajectoryRecomputeSeconds = 0.5f;
        private readonly JsonWriter _trajectoryJson = new JsonWriter(4096);
        private string _cachedTrajectoryFragment;
        private float _nextTrajectoryRecomputeAt = float.NegativeInfinity;

        private void WriteTrajectory(ICraftScript craft, ICraftFlightData flightData, ICraftOrbitData orbit)
        {
            if (_cachedTrajectoryFragment == null || Time.unscaledTime >= _nextTrajectoryRecomputeAt)
            {
                _nextTrajectoryRecomputeAt = Time.unscaledTime + TrajectoryRecomputeSeconds;
                _cachedTrajectoryFragment = ComputeTrajectoryFragment(craft, flightData, orbit);
            }

            _json.AppendRaw(_cachedTrajectoryFragment);
        }

        private string ComputeTrajectoryFragment(ICraftScript craft, ICraftFlightData flightData, ICraftOrbitData orbit)
        {
            _trajectoryJson.Reset();
            _trajectoryJson.StartObject();

            IPlanetNode planetNode = orbit?.Parent;
            IPlanetData planetData = planetNode?.PlanetData;
            if (planetNode == null || planetData == null)
            {
                _trajectoryJson.Prop("trajectoryAvailable", false);
                LogTrajectoryDiagOnce("[Vizzy trajectory diag] unavailable: planetNode or planetData is null");
                return FinishTrajectoryFragment();
            }

            double mu = planetData.SurfaceGravity * planetData.Radius * planetData.Radius;
            Vector3d r = flightData.Position;
            Vector3d v = flightData.Velocity;
            double rMag = r.magnitude;
            double vMag = v.magnitude;

            if (mu <= 0d || rMag <= 0d || rMag < planetData.Radius)
            {
                // rMag < planet radius means the position is below the
                // surface - not a real orbit to draw a trajectory for (a
                // grounded, near-stationary craft's near-zero velocity still
                // produces *some* technically-computed semi-major axis via
                // the vis-viva equation, describing an "orbit" whose
                // periapsis is deep inside the planet - meaningless to draw).
                _trajectoryJson.Prop("trajectoryAvailable", false);
                LogTrajectoryDiagOnce($"[Vizzy trajectory diag] unavailable: mu={mu:F0} rMag={rMag:F0} planetRadius={planetData.Radius:F0}");
                return FinishTrajectoryFragment();
            }

            // Only used here to work out the eccentricity (is this an escape
            // trajectory?) and the semi-major axis (how wide a time window
            // makes sense to sample) - not to derive a full element set, see
            // this method's own header comment for why.
            double energy = vMag * vMag / 2d - mu / rMag;
            double a = -mu / (2d * energy);
            Vector3d eVec = Subtract(
                Scale(r, vMag * vMag - mu / rMag),
                Scale(v, Dot(r, v)));
            eVec = Scale(eVec, 1d / mu);
            double e = eVec.magnitude;

            // a<0 is expected and fine for a hyperbolic orbit (e>=1) - only
            // reject a near-zero a, which would mean energy is right at the
            // escape threshold and the window size below isn't well-behaved.
            if (System.Math.Abs(a) < 1d)
            {
                _trajectoryJson.Prop("trajectoryAvailable", false);
                LogTrajectoryDiagOnce($"[Vizzy trajectory diag] unavailable: e={e:F3} a={a:F0}");
                return FinishTrajectoryFragment();
            }

            var orbitNode = craft?.CraftNode as IOrbitNode;
            IOrbitPoint currentPoint = orbitNode?.GetCurrentPoint();
            if (orbitNode == null || currentPoint == null)
            {
                _trajectoryJson.Prop("trajectoryAvailable", false);
                LogTrajectoryDiagOnce(
                    "[Vizzy trajectory diag] unavailable: craft.CraftNode is not an IOrbitNode (or GetCurrentPoint() " +
                    "returned null) - the GetPointAtTime approach needs this cast to succeed.");
                return FinishTrajectoryFragment();
            }

            double nowTime = currentPoint.Time;
            double rotationNowDeg = planetNode.RotationAngle * Mathf.Rad2Deg;
            double angularVelDeg = planetData.AngularVelocity * Mathf.Rad2Deg;

            bool hyperbolic = e >= 1d;
            double fromSec, toSec;
            if (hyperbolic)
            {
                // No periapsis-to-periapsis period to scale off for an
                // escape trajectory - 1/meanMotion (meanMotion = sqrt(mu/
                // |a|^3), the hyperbolic analog of the circular formula,
                // well-defined for a<0 too) is used as the natural
                // timescale instead. The true anomaly only crawls toward
                // its asymptotic limit logarithmically in time, so even a
                // generous window like this stays well short of it - no
                // risk of sampling out near where the radius blows up.
                double meanMotion = System.Math.Sqrt(mu / System.Math.Abs(a * a * a));
                double timeScale = 1d / meanMotion;
                fromSec = -2d * timeScale;
                toSec = 6d * timeScale;
            }
            else
            {
                fromSec = -0.5d * orbit.Period;
                toSec = 1.5d * orbit.Period;
            }

            _trajectoryJson.Prop("trajectoryAvailable", true);
            int pastWritten = WriteGroundTrack("trajPast", orbitNode, nowTime, fromSec, 0d, hyperbolic ? 180 : 140, rotationNowDeg, angularVelDeg);
            int futureWritten = WriteGroundTrack("trajFuture", orbitNode, nowTime, 0d, toSec, hyperbolic ? 520 : 400, rotationNowDeg, angularVelDeg);

            LogTrajectoryDiagOnce(
                $"[Vizzy trajectory diag] using GetPointAtTime: hyperbolic={hyperbolic} e={e:F3} a={a:F0} " +
                $"window=[{fromSec:F0}s,{toSec:F0}s] nowTime={nowTime:F1} rotationNow={rotationNowDeg:F1} " +
                $"pastPoints={pastWritten} futurePoints={futureWritten}");

            return FinishTrajectoryFragment();
        }

        // _trajectoryJson was opened with a bare StartObject() purely so its
        // normal Prop/StartArray API would separate entries with commas
        // correctly - that wrapping "{"/"}" isn't wanted once the result is
        // spliced into the real frame object via AppendRaw, so it's trimmed
        // off here rather than in every call site above.
        private string FinishTrajectoryFragment()
        {
            _trajectoryJson.EndObject();
            string json = _trajectoryJson.ToString();
            return json.Substring(1, json.Length - 2);
        }

        // Writes a flat [lat, lon, lat, lon, ...] array (degrees) for dt in
        // [fromSec, toSec] relative to nowTime, sampled at `steps`+1 points.
        // Returns how many points were actually written, for the diagnostic
        // log - if this comes back 0, GetPointAtTime itself is the problem,
        // not the lat/lon conversion.
        private int WriteGroundTrack(string key, IOrbitNode orbitNode, double nowTime, double fromSec, double toSec, int steps, double rotationNowDeg, double angularVelDeg)
        {
            _trajectoryJson.StartArray(key);

            int written = 0;
            for (int i = 0; i <= steps; i++)
            {
                double dt = fromSec + (toSec - fromSec) * (i / (double)steps);

                IOrbitPoint point;
                try
                {
                    point = orbitNode.GetPointAtTime(nowTime + dt);
                }
                catch (System.Exception)
                {
                    continue;
                }

                if (point == null)
                {
                    continue;
                }

                Vector3d pos = point.Position;
                double mag = pos.magnitude;
                if (!(mag > 0d))
                {
                    continue;
                }

                // Same conversion WriteFlightState uses for the live marker,
                // confirmed correct against a known real-world position:
                // inertial lat/lon via asin/atan2, then + the planet's own
                // rotation angle AT THIS SPECIFIC TIME (not "now") to land
                // in the body-fixed frame. Negated x: proven live -
                // IOrbitNode's Position mirrors X relative to
                // flightData.Position (what the marker itself uses).
                double lat = System.Math.Asin(Clamp(pos.y / mag, -1d, 1d)) * Mathf.Rad2Deg;
                double lonInertial = System.Math.Atan2(-pos.x, pos.z) * Mathf.Rad2Deg;
                double lon = lonInertial + rotationNowDeg + angularVelDeg * dt;
                lon = ((lon + 180d) % 360d + 360d) % 360d - 180d;

                _trajectoryJson.Value(lat);
                _trajectoryJson.Value(lon);
                written++;
            }

            _trajectoryJson.EndArray();
            return written;
        }

        // Throttled, not once per frame (this runs at console refresh rate)
        // - just enough to confirm in the Player log whether the element
        // derivation succeeded and see the raw numbers, without spamming it
        // every tick. Repeats every DiagRepeatSeconds rather than only ever
        // firing once, so it stays useful later in a flight too (a strictly
        // one-shot version of this previously went silent right after
        // launch, well before reaching the in-orbit state that needed
        // checking).
        private void LogTrajectoryDiagOnce(string message)
        {
            if (Time.unscaledTime < _nextTrajectoryDiagAt)
            {
                return;
            }

            _nextTrajectoryDiagAt = Time.unscaledTime + DiagRepeatSeconds;
            Log.Info(message);
        }

        private static Vector3d Cross(Vector3d a, Vector3d b)
        {
            return new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        }

        private static double Dot(Vector3d a, Vector3d b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        private static Vector3d Scale(Vector3d v, double s)
        {
            return new Vector3d(v.x * s, v.y * s, v.z * s);
        }

        private static Vector3d Subtract(Vector3d a, Vector3d b)
        {
            return new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        // Not System.Math.Atanh - that overload isn't available on every
        // .NET/Mono runtime this could end up running on. Standard identity.
        private static double Atanh(double x)
        {
            return 0.5d * System.Math.Log((1d + x) / (1d - x));
        }

        private void WriteAttitude(ICraftFlightData flightData)
        {
            _json.Prop("pitch", flightData.Pitch);
            _json.Prop("heading", flightData.Heading);
            _json.Prop("roll", flightData.BankAngle);
            _json.Prop("aoa", flightData.AngleOfAttack);

            WriteUnitVector("cf", flightData.CraftForward);
            WriteUnitVector("cr", flightData.CraftRight);
            WriteUnitVector("cu", flightData.CraftUp);

            // Inside an atmosphere the surface frame is the useful one; above it the
            // orbital frame is, which matches what Juno's own navball shows.
            bool inAtmosphere = flightData.AltitudeAboveSeaLevel < flightData.AtmosphereSample.AtmosphereHeight;
            WriteUnitVector("prograde", inAtmosphere ? flightData.SurfaceVelocity : flightData.Velocity);

            var target = flightData.NavSphereTarget;
            if (target != null && !target.IsDestroyed)
            {
                WriteUnitVector("targetDir", target.Position - flightData.Position);
            }
            else
            {
                _json.PropNull("targetDir");
            }
        }

        private void WriteControls(ICraftScript craft)
        {
            var controls = craft.ActiveCommandPod?.Controls;
            _json.Prop("throttle", controls != null ? controls.Throttle : 0d);
            _json.Prop("translationMode", controls != null && controls.TranslationModeEnabled);
        }

        private void WriteUnitVector(string key, Vector3d vector)
        {
            double magnitude = vector.magnitude;
            if (magnitude < 1e-9)
            {
                _json.PropVector(key, 0d, 0d, 0d);
                return;
            }

            _json.PropVector(key, vector.x / magnitude, vector.y / magnitude, vector.z / magnitude);
        }

        private void RefreshGroupNames(ICommandPod pod)
        {
            if (Time.unscaledTime - _groupNamesRefreshedAt < GroupNameRefreshSeconds)
            {
                return;
            }

            _groupNamesRefreshedAt = Time.unscaledTime;
            _groupNames.Clear();

            var controls = pod.Controls;
            if (controls == null)
            {
                return;
            }

            for (int group = 1; group <= ActivationGroupCount; group++)
            {
                _groupNames.Add(controls.GetActivationGroupName(group) ?? string.Empty);
            }
        }

        private static string GetCraftName(ICraftNode craftNode)
        {
            if (craftNode is ModApi.Flight.Sim.IOrbitNode orbitNode && !string.IsNullOrEmpty(orbitNode.Name))
            {
                return orbitNode.Name;
            }

            if (craftNode?.InitialCraftNodeData != null)
            {
                foreach (var data in craftNode.InitialCraftNodeData)
                {
                    if (data != null && !string.IsNullOrEmpty(data.Name))
                    {
                        return data.Name;
                    }
                }
            }

            return "Craft";
        }

        private static int Count(System.Collections.IEnumerable items)
        {
            if (items == null)
            {
                return 0;
            }

            if (items is System.Collections.ICollection collection)
            {
                return collection.Count;
            }

            int count = 0;
            foreach (object unused in items)
            {
                count++;
            }

            return count;
        }
    }
}
