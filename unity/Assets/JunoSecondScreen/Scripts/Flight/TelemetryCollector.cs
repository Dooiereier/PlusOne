namespace JunoSecondScreen.Flight
{
    using System.Collections.Generic;
    using JunoSecondScreen.Util;
    using ModApi.Common;
    using ModApi.Craft;
    using ModApi.Craft.Parts;
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

        /// <summary>
        /// Builds one telemetry frame.
        /// </summary>
        /// <returns>A JSON document describing the current flight.</returns>
        public string Build()
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
            WriteFlightState(flightData);
            WritePerformance(craft, flightData);
            WriteOrbit(flightData);
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

        private void WriteFlightState(ICraftFlightData flightData)
        {
            var planetData = flightData.Orbit?.Parent?.PlanetData;
            var atmosphere = flightData.AtmosphereSample;

            _json.Prop("planet", planetData != null ? planetData.Name : "—");
            _json.Prop("planetRadius", planetData != null ? planetData.Radius : 0d);

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

            var planetNode = flightData.Orbit?.Parent;
            if (planetNode != null)
            {
                planetNode.GetSurfaceCoordinates(flightData.PositionNormalized, out double latitude, out double longitude);
                _json.Prop("latitude", latitude * Mathf.Rad2Deg);
                _json.Prop("longitude", longitude * Mathf.Rad2Deg);
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

        private void WriteOrbit(ICraftFlightData flightData)
        {
            ICraftOrbitData orbit = flightData.Orbit;
            _json.Prop("apoapsis", orbit != null ? orbit.ApoapsisAltitude : 0d);
            _json.Prop("periapsis", orbit != null ? orbit.PeriapsisAltitude : 0d);
            _json.Prop("timeToAp", orbit != null ? orbit.ApoapsisTime : 0d);
            _json.Prop("timeToPe", orbit != null ? orbit.PeriapsisTime : 0d);
            _json.Prop("eccentricity", orbit != null ? orbit.Eccentricity : 0d);
            _json.Prop("inclination", orbit != null ? orbit.Inclination * Mathf.Rad2Deg : 0d);
            _json.Prop("period", orbit != null ? orbit.Period : 0d);
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
