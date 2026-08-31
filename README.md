# Juno Second Screen

Turn an iPad — or any tablet, phone or laptop with a browser — into a second
screen for **Juno: New Origins**.

The mod runs a small web server inside the game. Point Safari at your PC's
address and the tablet becomes a touch flight console: navball, gauges, orbit
plot, resource bars, staging, throttle and activation groups, plus an optional
live view of the game window. No App Store app, no cables, no extra software on
the iPad.

```
   PC running Juno                     iPad on the same Wi-Fi
  ┌──────────────────┐                ┌──────────────────────┐
  │ game + mod       │  telemetry ──▶ │  Safari              │
  │  HTTP :8088      │                │   navball, gauges,   │
  │  WebSocket /ws   │ ◀── commands   │   staging, throttle  │
  │  MJPEG /stream   │  ──── video ─▶ │   live view          │
  └──────────────────┘                └──────────────────────┘
```

## What it gives you

**Flight tab** — navball with prograde/retrograde/target markers and a heading
tape, altitude ASL/AGL, surface/orbital/vertical/horizontal speed, g-force,
Mach, apoapsis/periapsis with time to each, TWR, stage ΔV, thrust, mass, Isp,
remaining burn time, engine and stage counts, atmospheric pressure and density,
latitude/longitude, and fuel/monopropellant/battery bars.

**Orbit tab** — apoapsis, periapsis, time to each, eccentricity, inclination and
period, drawn as a live orbit plot with the planet, the edge of the atmosphere
and your position on the conic.

**View tab** — an optional MJPEG stream of the game window, so you can glance at
the rocket while your eyes are on the console. Off by default per client; it
costs nothing while nobody is watching it.

**Controls** — throttle slider, a big STAGE button, all ten activation groups
with their in-game names, RCS translation toggle, brake, time warp up/down,
pause, and navball heading locks (prograde, retrograde, target, manoeuvre node,
free). Control can be disabled entirely for a read-only console.

## Install

1. Download `Second Screen.sr2-mod` from the releases, or build it yourself
   (see [docs/BUILD.md](docs/BUILD.md)).
2. Copy it into Juno's mods folder:
   - **Windows:** `%USERPROFILE%\AppData\LocalLow\Jundroo\SimpleRockets 2\Mods`
   - **macOS:** `~/Library/Application Support/Jundroo/SimpleRockets 2/Mods`
3. Start Juno and enable **Second Screen** in the Mods menu.

## Connect the iPad

1. Put the iPad and the PC on the same Wi-Fi network.
2. Start a flight. The address appears on screen for a few seconds, and is
   always written to Juno's log:
   `Second screen: http://192.168.1.20:8088/?t=k7prq2wf`
3. Open that address in Safari. Tap **Share → Add to Home Screen** to get a
   full-screen icon without the browser chrome.

The `?t=` token stops anything else on your network from driving your rocket.
It is stored once and stays the same, so the home-screen shortcut keeps working.
Turn it off under **Settings → Mods → Second Screen** if you would rather not
bother on a network you trust.

If Windows asks whether to allow Juno through the firewall when the server
starts, say yes for **private networks** — otherwise the iPad cannot reach it.

## Settings

Found under **Settings → Mods → Second Screen**. Changes take effect within a
second; no restart needed.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | on | Runs the server. |
| Port | 8088 | Change if something else already uses this port. |
| Require access token | on | Only devices that open the `?t=...` address may connect. |
| Allow control input | on | Off makes the console read-only. |
| Telemetry rate | 15 Hz | Frames per second sent to the tablet. |
| Enable video feed | on | Whether the View tab may stream at all. |
| Video width | 640 px | Frames are scaled to this width before sending. |
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
- The console's HTML, CSS and JavaScript live in [`web/`](web) and are baked into
  the mod assembly by [`tools/build_web_assets.py`](tools/build_web_assets.py),
  so the mod stays a single file to install.

## What this is not

This is a **companion console**, not an operating-system second display: Juno
still renders on your PC, and the iPad shows instruments (plus an optional video
feed) rather than becoming a monitor Windows can extend onto.

If you want a genuine extended desktop, use Sidecar (macOS) or Duet/Luna Display
(Windows) to make the iPad a real display first, then a multi-display mod such as
PigeonEye can put the map view on it. The two approaches complement each other —
this mod is the one that works with nothing installed on the iPad.

## Building and testing

See [docs/BUILD.md](docs/BUILD.md) for the Unity build, and
[docs/PROTOCOL.md](docs/PROTOCOL.md) for the wire format if you want to write
your own client.

Most of the mod can be checked without opening Unity or launching the game:

```bash
# HTTP, WebSocket, MJPEG and JSON against the real classes, then the baked
# console driven in headless Chromium at iPad size against a simulated ascent.
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
