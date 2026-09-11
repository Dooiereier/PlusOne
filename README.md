# PlusOne

Turn any tablet, phone or laptop with a browser into a second screen for
**Juno: New Origins**: a full flight console, an orbit map, an MFD mirror you
can actually tap, and a live craft-camera feed.

The mod runs a small web server inside the game. Point a browser at your PC's
address and the device becomes a touch console: navball, gauges, orbit ground
track, MFD mirroring, resource bars, staging, throttle, activation groups and
time warp, plus an optional live view of the game window or any craft camera.
No app-store app, no cables, no extra software on the other device.

```
   PC running Juno                     Tablet on the same Wi-Fi
  ┌──────────────────┐                ┌──────────────────────┐
  │ game + mod       │  telemetry ──▶ │  Browser             │
  │  HTTP :8088      │                │   navball, gauges,   │
  │  WebSocket /ws   │ ◀── commands   │   MFD, staging,      │
  │  MJPEG streams   │  ──── video ─▶ │   throttle, cameras  │
  └──────────────────┘                └──────────────────────┘
```

## What it gives you

**Flight tab** — navball with prograde/retrograde/target markers and
independent pitch/roll/yaw readouts, altitude ASL/AGL, surface/orbital/
vertical/horizontal speed, g-force, Mach, apoapsis/periapsis with time to
each, TWR, stage ΔV, thrust, mass, Isp, remaining burn time, engine and stage
counts, atmospheric pressure and density, latitude/longitude,
fuel/monopropellant/battery bars, and time warp controls (rate readout,
up/down/pause).

**Orbit tab** — apoapsis, periapsis, time to each, eccentricity, inclination,
period and orbited body, drawn on a live equirectangular map of the planet
with your past and future ground track traced on it and your current
position marked.

**MFD tab** — mirrors any multi-function display on the craft as a live
image, and taps on it are forwarded back into the game as if you had clicked
the physical MFD yourself — buttons, page switches, everything.

**Cameras tab** — an optional live view of the game window, or any of the
craft's own camera-vantage parts (nose cam, docking cam, etc.) picked from a
dropdown. Off by default per client; costs nothing while nobody is watching
a feed.

**Controls** — throttle slider, a big STAGE button, all ten activation groups
with their in-game names, RCS translation toggle, brake, and navball heading
locks (prograde, retrograde, target, manoeuvre node, free). Control can be
disabled entirely for a read-only console.

## Install

1. Download `PlusOne.sr2-mod` from the releases, or build it yourself
   (see [docs/BUILD.md](docs/BUILD.md)).
2. Copy it into Juno's mods folder:
   - **Windows:** `%USERPROFILE%\AppData\LocalLow\Jundroo\SimpleRockets 2\Mods`
   - **macOS:** `~/Library/Application Support/Jundroo/SimpleRockets 2/Mods`
3. Start Juno. There's nothing to enable in the Mods menu — the console is
   turned on from inside a flight instead (see below).

## Connect the console

1. Put the tablet (or phone/laptop) and the PC on the same Wi-Fi network.
2. Start a flight, open the **Flight Info** panel, expand the **PlusOne**
   group, and tap **Enabled**. It always starts off — you turn it on for
   each flight yourself, and it turns itself back off when you leave the
   flight scene.
3. The address appears on screen for a few seconds after you turn it on, and
   is always written to Juno's log:
   `PlusOne: http://192.168.1.20:8088/?t=k7prq2wf`
4. Open that address in the device's browser. Most mobile browsers offer an
   "Add to Home Screen" option (Share menu on iOS/Safari, ⋮ menu on
   Android/Chrome) for a full-screen icon without the browser chrome.

The `?t=` token stops anything else on your network from driving your rocket.
It is stored once and stays the same, so the home-screen shortcut keeps working.
Turn it off under **Settings → Mods → PlusOne** if you would rather not
bother on a network you trust.

If Windows asks whether to allow Juno through the firewall when the server
starts, say yes for **private networks** — otherwise the other device cannot
reach it.

## Settings

Found under **Settings → Mods → PlusOne**. Changes take effect within a
second; no restart needed. There's no on/off switch here — that's the Flight
Info panel's job (see [Connect the console](#connect-the-console)); everything
below just configures how the console behaves once it's running.

| Setting | Default | What it does |
| --- | --- | --- |
| Port | 8088 | Change if something else already uses this port. |
| Require access token | on | Only devices that open the `?t=...` address may connect. |
| Allow control input | on | Off makes the console read-only. |
| Telemetry rate | 15 Hz | Frames per second sent to the tablet. |
| Enable video feed | on | Whether the MFD/View/Cameras tabs may stream video at all. |
| Video width | 1280 px | Frames are scaled to this width before sending. |
| Video frame rate | 12 fps | Video frames per second. |
| Video quality | 60 | JPEG quality of the video feed. |

## How it works

The mod adds a persistent `MonoBehaviour` that owns a `TcpListener`-based HTTP
server. (`HttpListener` is avoided deliberately: on Windows it needs a URL ACL
or administrator rights for any address other than localhost, which would put
the tablet out of reach.)

- Telemetry is read from `ModApi` on the Unity main thread, serialized with a
  small allocation-conscious JSON writer, and pushed over a WebSocket. Reader
  threads block on a condition variable, so a frame reaches the tablet as soon
  as it is built rather than on a polling interval.
- Commands arrive on the same socket, are parsed on the network thread, queued,
  and applied on the main thread in `Update`. Throttle and brake are re-applied
  for a third of a second after each message so the value sticks, then released
  again — the keyboard on the PC keeps working normally.
- Video frames are captured with `ScreenCapture.CaptureScreenshotIntoRenderTexture`,
  downscaled and flipped in one blit, pulled off the GPU with
  `AsyncGPUReadback`, and JPEG-encoded on a worker thread. No render textures are
  allocated and no frames are captured while nobody is watching the feed.
- The MFD and craft-camera feeds work the same way, but from a dedicated
  off-screen camera pointed at the MFD's canvas or the vantage part instead of
  a full screenshot, so they cost nothing on any part of the craft nobody has
  picked to watch.
- Taps on the MFD image are translated from the streamed image's normalized
  coordinates into a hit test against the MFD's own widget hierarchy (no
  raycast, since the console's capture camera isn't the player's own), then
  dispatched through the game's own pointer-event interface — indistinguishable
  from a real click as far as the MFD's own code is concerned.
- The Orbit tab's ground track is sampled directly from the game's own orbit
  simulation (`IOrbitNode.GetPointAtTime`) rather than a hand-rolled orbital
  mechanics model, so it stays exact through burns and time warp; sampling is
  capped to twice a second and only runs while a client actually has the tab
  open.
- The console's HTML, CSS and JavaScript live in [`web/`](web) and are baked into
  the mod assembly by [`tools/build_web_assets.py`](tools/build_web_assets.py),
  so the mod stays a single file to install.

## What this is not

This is a **companion console**, not an operating-system second display: Juno
still renders on your PC, and the other device shows instruments (plus an
optional video feed) rather than becoming a monitor Windows can extend onto.

If you want a genuine extended desktop, use Sidecar (macOS) or Duet/Luna Display
(Windows) to make a tablet a real display first, then a multi-display mod such as
PigeonEye can put the map view on it. The two approaches complement each other —
this mod is the one that works with nothing installed on the other device.

## Building and testing

See [docs/BUILD.md](docs/BUILD.md) for the Unity build, and
[docs/PROTOCOL.md](docs/PROTOCOL.md) for the wire format if you want to write
your own client.

Most of the mod can be checked without opening Unity or launching the game:

```bash
# HTTP, WebSocket, MJPEG and JSON against the real classes, then the baked
# console driven in headless Chromium at tablet size against a simulated ascent.
./tests/run_tests.sh

# Compile the whole mod, ModApi calls included, against the game's own assemblies.
MOD_TOOLS_ASSEMBLIES=~/JunoMod/Assets/ModTools/Assemblies ./tests/typecheck.sh
```

The transport tests cover the RFC 6455 handshake, masked and fragmented client
frames, extended payload lengths, ping/pong, keep-alive and MJPEG part framing.
The console test checks that telemetry renders, the navball and orbit canvases
draw, the layout survives a portrait viewport, and that touching the controls
sends the right commands back.

## License

MIT — see [LICENSE](LICENSE).
