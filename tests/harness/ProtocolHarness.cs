namespace JunoSecondScreen.Tests.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using JunoSecondScreen.Net;
    using JunoSecondScreen.Util;

    internal static class ProtocolHarness
    {
        private static readonly List<string> Received = new List<string>();
        private static int _frameCounter;

        private static void Main()
        {
            RunJsonTests();

            var server = new HttpServer(Handle);
            if (!server.Start(18088))
            {
                Console.WriteLine("FAIL: server did not start");
                return;
            }

            Console.WriteLine("READY " + server.Port);
            Console.Out.Flush();

            while (Console.ReadLine() is string line && line != "quit")
            {
                if (line == "received")
                {
                    lock (Received)
                    {
                        Console.WriteLine("RECEIVED " + string.Join("|", Received.ToArray()));
                    }

                    Console.Out.Flush();
                }
            }

            server.Stop();
            Console.WriteLine("STOPPED");
        }

        private static void Handle(HttpRequest request, HttpConnection connection)
        {
            switch (request.Path)
            {
                case "/":
                    connection.Respond(
                        200,
                        "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes("<h1>hello " + (request.GetQuery("t") ?? "none") + "</h1>"),
                        new[] { new KeyValuePair<string, string>("Set-Cookie", "jss=abc; Path=/") });
                    return;

                case "/api/command":
                    lock (Received)
                    {
                        Received.Add("post:" + request.Body);
                    }

                    connection.Respond(204, "text/plain", Array.Empty<byte>());
                    return;

                case "/ws":
                    ServeWebSocket(request, connection);
                    return;

                case "/stream.mjpg":
                    ServeStream(connection);
                    return;

                default:
                    connection.RespondText(404, "text/plain", "Not found");
                    return;
            }
        }

        private static void ServeWebSocket(HttpRequest request, HttpConnection connection)
        {
            if (!request.IsWebSocketUpgrade())
            {
                connection.RespondText(400, "text/plain", "expected upgrade");
                return;
            }

            WebSocketConnection socket = WebSocketConnection.Accept(request, connection);
            socket.SendText("{\"type\":\"hello\",\"control\":true}");

            var pusher = new Thread(() =>
            {
                int n = 0;
                while (socket.IsOpen && n < 200)
                {
                    var json = new JsonWriter(128);
                    json.StartObject();
                    json.Prop("type", "telemetry");
                    json.Prop("n", n++);
                    json.Prop("text", "quote\" backslash\\ unicodeé");
                    json.PropVector("cf", 0.5, -0.25, 1d / 3d);
                    json.EndObject();
                    if (!socket.SendText(json.ToString()))
                    {
                        return;
                    }

                    Thread.Sleep(20);
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

                object parsed = JsonReader.Parse(message);
                string summary = parsed is Dictionary<string, object> map
                    ? "ws:" + JsonReader.GetString(map, "cmd") + "=" + JsonReader.GetDouble(map, "v").ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    : "ws:unparsed";
                lock (Received)
                {
                    Received.Add(summary);
                }
            }

            socket.Close();
        }

        private static void ServeStream(HttpConnection connection)
        {
            connection.BeginMjpeg();
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    var frame = new byte[64];
                    frame[0] = 0xFF;
                    frame[1] = 0xD8;
                    frame[63] = (byte)Interlocked.Increment(ref _frameCounter);
                    connection.WriteMjpegFrame(frame);
                    Thread.Sleep(30);
                }
            }
            finally
            {
                connection.Close();
            }
        }

        private static void RunJsonTests()
        {
            var writer = new JsonWriter();
            writer.StartObject();
            writer.Prop("type", "telemetry");
            writer.Prop("inFlight", true);
            writer.Prop("alt", 1234.56789d);
            writer.Prop("nan", double.NaN);
            writer.Prop("name", "Fal\"con\\9\n");
            writer.PropVector("cf", 1d, 0d, -1d);
            writer.PropNull("targetDir");
            writer.StartArray("groups");
            for (int i = 1; i <= 2; i++)
            {
                writer.StartObject();
                writer.Prop("i", i);
                writer.Prop("on", i == 1);
                writer.EndObject();
            }

            writer.EndArray();
            writer.StartObject("video");
            writer.Prop("width", 640);
            writer.EndObject();
            writer.EndObject();

            Console.WriteLine("JSON " + writer.ToString());

            var roundTrip = JsonReader.Parse(writer.ToString()) as Dictionary<string, object>;
            Console.WriteLine("PARSE-OK " + (roundTrip != null && JsonReader.GetString(roundTrip, "name") == "Fal\"con\\9\n"));
            Console.WriteLine("PARSE-BAD " + (JsonReader.Parse("{\"a\":") == null) + " " + (JsonReader.Parse("nope") == null));
            Console.Out.Flush();
        }
    }
}
