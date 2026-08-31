# Building the mod

## What you need

- **Juno: New Origins** with the Mod Tools installed. On Steam the package is at
  `steamapps\common\SimpleRockets2\ModTools\SimpleRockets2_ModTools.unitypackage`
  (install "Mod Tools" from the game's Steam properties → DLC if it is missing).
- **Unity 2022.3.x** — the game is built with 2022.3, and matching it avoids
  asset bundle surprises. Any 2022.3 patch release works.
- **Python 3** — only if you change anything under `web/`.

## First build

1. Create a new empty Unity 2022.3 project.
2. `Assets → Import Package → Custom Package…`, choose
   `SimpleRockets2_ModTools.unitypackage`, import everything.
3. Copy this repository's `unity/Assets/JunoSecondScreen` folder and
   `unity/Assets/ModData.asset` into the project's `Assets` folder.
4. Unity compiles `JunoSecondScreen.dll` from the assembly definition. The
   console errors you might see at this point are almost always a missing Mod
   Tools import, not the mod code.
5. Open `ModData.asset` in the inspector and check that `_assemblies` lists
   `JunoSecondScreen.dll`. If the asset shows a missing script, delete it and
   create a fresh one from the Mod Tools menu, then fill in the same fields (the
   values are listed in the file, which is plain YAML).
6. Build with the Mod Tools menu. The package lands in `ModAssetBundles/` as
   `Second Screen.sr2-mod`.
7. `Tools → Second Screen → Deploy Built Mod To Juno` copies it into the game's
   mods folder. Juno must be closed, since it holds the file open.

## Changing the console

The tablet UI is authored as ordinary web files in `web/`:

| File | Contents |
| --- | --- |
| `web/index.html` | Panel and control layout |
| `web/app.css` | Theme, grid layout, responsive breakpoints |
| `web/app.js` | WebSocket client, formatting, navball and orbit canvases, touch input |
| `web/icon.png` | Home screen icon, drawn by `tools/make_icon.py` |

After editing, re-bake them into the assembly:

```bash
python3 tools/build_web_assets.py
```

That rewrites `unity/Assets/JunoSecondScreen/Scripts/Web/WebAssets.g.cs`, which
is committed so that a Unity-only checkout still builds. Do not edit the
generated file by hand.

To iterate on the UI without launching the game, run the console test — it
serves the baked assets against a simulated ascent and leaves screenshots in
`tests/build/screenshots`:

```bash
./tests/run_tests.sh
```

## Layout of the C# code

```
Scripts/
  Mod.cs                  Mod entry point; creates the persistent service object
  ModSettings.cs          The mod's page in Juno's settings screen
  ModConfiguration.cs     Immutable settings snapshot; drives live restarts
  SecondScreenService.cs  Server lifecycle, routing, auth, telemetry publishing
  DeployTools.cs          Editor-only menu items (guarded by UNITY_EDITOR)
  Flight/
    TelemetryCollector.cs Reads ModApi and writes the telemetry frame
    CommandProcessor.cs   Queues console commands, applies them on the main thread
    ViewCapture.cs        Screen capture, async readback and JPEG encoding
  Net/
    HttpServer.cs         TcpListener accept loop and connection threads
    HttpConnection.cs     Response writing, MJPEG framing, stream handover
    HttpRequest.cs        Request line, headers, query string
    SocketReader.cs       Buffered line and exact-length reads
    WebSocketConnection.cs RFC 6455 handshake and framing
    NetworkUtil.cs        Picks the LAN addresses worth showing the player
  Util/
    JsonWriter.cs         Append-only JSON writer
    JsonReader.cs         Small JSON parser for incoming commands
    Log.cs                Prefixed logging
  Web/
    WebAssets.g.cs        Generated: the baked console
```

Everything under `Net/`, `Util/` and `Web/` is engine independent, which is what
lets `tests/run_tests.sh` compile and exercise it with Mono outside Unity.
