namespace JunoSecondScreen.Tests.Console
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using JunoSecondScreen.Net;
    using JunoSecondScreen.Util;
    using JunoSecondScreen.Web;

    /// <summary>
    /// Serves the real baked console against a simulated ascent so the front end can
    /// be driven by a browser without the game running.
    /// </summary>
    internal static class ConsoleHarness
    {
        private static readonly List<string> Commands = new List<string>();
        private static readonly DateTime Start = DateTime.UtcNow;

        private static void Main()
        {
            var server = new HttpServer(Handle);
            server.Start(18099);
            Console.WriteLine("READY " + server.Port);
            Console.Out.Flush();

            while (Console.ReadLine() is string line && line != "quit")
            {
                if (line == "commands")
                {
                    lock (Commands)
                    {
                        Console.WriteLine("COMMANDS " + string.Join(" ", Commands.ToArray()));
                    }

                    Console.Out.Flush();
                }
            }

            server.Stop();
        }

        private static void Handle(HttpRequest request, HttpConnection connection)
        {
            if (request.Path == "/ws")
            {
                WebSocketConnection socket = WebSocketConnection.Accept(request, connection);
                if (socket == null)
                {
                    return;
                }

                socket.SendText("{\"type\":\"hello\",\"control\":true,\"video\":{\"width\":640,\"fps\":12,\"quality\":60}}");
                var pusher = new Thread(() =>
                {
                    while (socket.IsOpen)
                    {
                        if (!socket.SendText(BuildTelemetry()))
                        {
                            return;
                        }

                        Thread.Sleep(66);
                    }
                })
                { IsBackground = true };
                pusher.Start();

                while (true)
                {
                    string message = socket.ReceiveText();
                    if (message == null)
                    {
                        break;
                    }

                    if (JsonReader.Parse(message) is Dictionary<string, object> map)
                    {
                        lock (Commands)
                        {
                            Commands.Add(JsonReader.GetString(map, "cmd", "?"));
                        }
                    }
                }

                socket.Close();
                return;
            }

            if (WebAssets.TryGet(request.Path, out byte[] content, out string contentType))
            {
                connection.Respond(200, contentType, content);
                return;
            }

            connection.RespondText(404, "text/plain", "Not found");
        }

        /// <summary>
        /// A simulated ascent from Droo, covering the same keys the real collector writes.
        /// </summary>
        private static string BuildTelemetry()
        {
            double t = (DateTime.UtcNow - Start).TotalSeconds;
            double altitude = 400d + (t * t * 60d);
            double speed = 30d + (t * 45d);
            const double PlanetRadius = 3200000d;

            var json = new JsonWriter(4096);
            json.StartObject();
            json.Prop("type", "telemetry");
            json.Prop("inFlight", true);
            json.Prop("craft", "Ares IV Heavy");
            json.Prop("warp", 1d);
            json.Prop("paused", false);
            json.Prop("met", t + 96d);
            json.Prop("planet", "Droo");
            json.Prop("planetRadius", PlanetRadius);
            json.Prop("altAsl", altitude);
            json.Prop("altAgl", altitude - 12d);
            json.Prop("surfaceSpeed", speed);
            json.Prop("orbitalSpeed", speed + 220d);
            json.Prop("verticalSpeed", speed * 0.82d);
            json.Prop("horizontalSpeed", speed * 0.4d);
            json.Prop("mach", speed / 320d);
            json.Prop("radius", PlanetRadius + altitude);
            json.Prop("gForce", 1.6d + (0.2d * Math.Sin(t)));
            json.Prop("airPressure", 101325d * Math.Exp(-altitude / 8500d));
            json.Prop("airDensity", 1.225d * Math.Exp(-altitude / 8500d));
            json.Prop("atmosphereHeight", 60000d);
            json.Prop("latitude", 12.3456d);
            json.Prop("longitude", -48.9012d);
            json.Prop("fuel", Math.Max(0d, 1d - (t / 120d)));
            json.Prop("monoprop", 0.82d);
            json.Prop("battery", 0.64d);
            json.Prop("mass", 184000d - (t * 900d));
            json.Prop("twr", 1.42d);
            json.Prop("deltaV", 3450d - (t * 12d));
            json.Prop("isp", 312d);
            json.Prop("burnTime", 145d - t);
            json.Prop("thrust", 2600000d);
            json.Prop("maxThrust", 2600000d);
            json.Prop("activeEngines", 9);
            json.Prop("activeRcs", 0);
            json.Prop("stage", 2);
            json.Prop("stages", 4);

            json.StartArray("groups");
            string[] names = { "Fairing", "Gear", "Chutes", "Solar", "", "", "", "", "", "Abort" };
            for (int i = 1; i <= 10; i++)
            {
                json.StartObject();
                json.Prop("i", i);
                json.Prop("name", names[i - 1]);
                json.Prop("on", i == 2 || i == 4);
                json.EndObject();
            }

            json.EndArray();

            json.Prop("apoapsis", 210000d + (t * 900d));
            json.Prop("periapsis", -1200000d + (t * 8000d));
            json.Prop("timeToAp", 240d - (t % 240d));
            json.Prop("timeToPe", 2400d);
            json.Prop("eccentricity", 0.42d);
            json.Prop("inclination", 28.5d);
            json.Prop("period", 5400d);

            double pitch = Math.Max(12d, 88d - (t * 1.4d));
            json.Prop("pitch", pitch);
            json.Prop("heading", 92.5d);
            json.Prop("roll", 4d * Math.Sin(t * 0.7d));
            json.Prop("aoa", 1.2d);

            double pitchRad = pitch * Math.PI / 180d;
            json.PropVector("cf", Math.Cos(pitchRad), 0d, Math.Sin(pitchRad));
            json.PropVector("cr", 0d, 1d, 0d);
            json.PropVector("cu", -Math.Sin(pitchRad), 0d, Math.Cos(pitchRad));

            double progradePitch = pitchRad - 0.12d;
            json.PropVector("prograde", Math.Cos(progradePitch), 0.05d, Math.Sin(progradePitch));
            json.PropNull("targetDir");

            json.Prop("throttle", 0.92d);
            json.Prop("translationMode", false);
            json.EndObject();
            return json.ToString();
        }
    }
}
