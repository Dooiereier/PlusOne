# Tests

`run_tests.sh` builds the engine-independent parts of the mod with Mono and
exercises them without Unity or the game:

- **`protocol_test.py`** drives the real `HttpServer`, `WebSocketConnection`,
  `JsonWriter` and `JsonReader`: keep-alive, request bodies, the RFC 6455
  handshake, masked and fragmented client frames, extended payload lengths,
  ping/pong, and MJPEG part framing.
- **`console_test.py`** serves the baked console from `WebAssets.g.cs` against a
  simulated ascent, loads it in headless Chromium at iPad size, and checks that
  telemetry renders, the canvases draw, the layout survives a portrait viewport,
  and that touching the controls sends the expected commands back. Screenshots
  land in `build/screenshots`.

`harness/` holds the two small entry points that host the mod's classes for
these tests, plus a stub for `UnityEngine.Debug` so `Log.cs` compiles outside
Unity.

Anything that touches `ModApi` (telemetry collection, command application) or
Unity rendering (`ViewCapture`) needs the real game assemblies and is verified in
game instead.

## Type checking against the game

`typecheck.sh` compiles the *whole* mod — including `TelemetryCollector`,
`CommandProcessor`, `ViewCapture` and the settings page — against the game's own
`ModApi.dll` from the Mod Tools, using the small stand-ins in `stubs/` for the
Unity engine modules. It catches a wrong `ModApi` signature in seconds instead of
after a Unity import:

```bash
MOD_TOOLS_ASSEMBLIES=~/JunoMod/Assets/ModTools/Assemblies ./tests/typecheck.sh
```

The stubs exist only to satisfy the compiler; they contain no behaviour and are
never shipped. Unity builds the real thing.
