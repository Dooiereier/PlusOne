# Wire protocol

The console is just a web client, so anything that speaks HTTP and WebSocket can
drive it — a script, a Stream Deck, a second game view, your own dashboard.

Every request must carry the access token while **Require access token** is on,
either as the `t` query parameter or as a `jss=<token>` cookie. The token is
printed to Juno's log at startup and shown in the flight scene.

## Endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /` | The console page (also `/app.css`, `/app.js`, `/icon.png`, `/manifest.webmanifest`) |
| `GET /ws` | WebSocket: telemetry and MFD data down, commands up |
| `GET /stream.mjpg` | `multipart/x-mixed-replace` JPEG stream of the game window |
| `GET /mfd.mjpg?part=<name>` | JPEG stream of one MFD part's screen, by its in-craft name |
| `GET /camview.mjpg?camera=<name>` | JPEG stream from a craft camera-vantage part (nose cam, docking cam, …) |
| `GET /planetmap.png` | Equirectangular PNG of the orbited planet, cached per planet |
| `GET /api/status` | Server status as JSON |
| `POST /api/command` | A single command, for clients that would rather not hold a socket open |

All three MJPEG streams (`/stream.mjpg`, `/mfd.mjpg`, `/camview.mjpg`) only cost
anything on the server while at least one client is actually connected to that
specific stream — nothing is captured or encoded otherwise.

## Server → client messages

Sent as JSON text frames on the WebSocket.

### `hello`

Sent once when the socket opens.

```json
{"type":"hello","control":true,"video":{"width":1280,"fps":12,"quality":60}}
```

`control` is false when control input is disabled in the settings; `video` is
null when the video feed is disabled in the settings (this gates all three
MJPEG streams, not just `/stream.mjpg`).

### `telemetry`

Sent at the configured rate. When no craft is in flight, only `inFlight` is
present:

```json
{"type":"telemetry","inFlight":false}
```

Otherwise:

| Field | Meaning |
| --- | --- |
| `craft`, `planet` | Craft name and the body it orbits |
| `met`, `warp`, `paused` | Flight time in seconds, time multiplier, pause state |
| `altAsl`, `altAgl` | Altitude above sea level and ground level, metres |
| `surfaceSpeed`, `orbitalSpeed`, `verticalSpeed`, `horizontalSpeed` | m/s |
| `mach`, `gForce` | Mach number, acceleration in g |
| `radius`, `planetRadius` | Distance from the body's centre, and its radius |
| `planetRotationAngle`, `mapRotationAngle` | The planet's current spin, degrees, and its spin at the moment the cached `/planetmap.png` was generated - the Orbit tab needs both to keep the map and the ground track aligned |
| `airPressure`, `airDensity`, `atmosphereHeight` | Local atmosphere sample |
| `latitude`, `longitude` | Degrees |
| `fuel`, `monoprop`, `battery` | Remaining fractions, 0–1 |
| `mass`, `thrust`, `maxThrust`, `isp`, `twr`, `deltaV`, `burnTime` | Performance |
| `activeEngines`, `activeRcs`, `stage`, `stages` | Counts |
| `groups` | `[{"i":1,"name":"Fairing","on":false}, …]` for the ten activation groups |
| `apoapsis`, `periapsis`, `timeToAp`, `timeToPe`, `eccentricity`, `inclination`, `period` | Orbit; altitudes in metres, inclination in degrees |
| `trajectoryAvailable`, `trajPast`, `trajFuture` | Ground-track trail - see below |
| `pitch`, `heading`, `roll`, `aoa` | Attitude, degrees |
| `cf`, `cr`, `cu` | Craft forward/right/up unit vectors, `[x,y,z]` |
| `prograde` | Velocity direction, surface frame inside the atmosphere and orbital frame above it |
| `targetDir` | Unit vector to the navball target, or null |
| `throttle`, `translationMode` | Current control state |

The body-frame vectors are what make navball markers easy: project a direction
onto `cr` and `cu` for screen x and y, and use its dot product with `cf` to tell
whether the marker is in front of or behind the craft.

`trajPast`/`trajFuture` are only present when `trajectoryAvailable` is `true`,
which itself requires a connected client to have told the server it's on the
Orbit tab (see `orbitTabActive` below) - sampling the ground track is not free,
so the server skips it otherwise. Each is a flat array of degrees,
`[lat, lon, lat, lon, …]`, tracing the orbit's ground track from the game's own
orbit simulation (not a hand-propagated model, so it stays correct through
burns and warp) - `trajPast` back roughly half an orbit (or a fixed multiple of
the escape timescale for a hyperbolic orbit), `trajFuture` forward roughly one
and a half orbits. The whole trajectory is recomputed at most twice a second
and reused between recomputes, independent of the telemetry rate.

### `mfd`

Sent at up to 4 Hz while any client is connected.

```json
{"type":"mfd","mfdFrameVersion":0,"mainViewFrameVersion":0,"externalViewFrameVersion":0,"mfds":[],"cameras":["Nose Cam","Dock Cam"]}
```

| Field | Meaning |
| --- | --- |
| `mfdFrameVersion`, `mainViewFrameVersion`, `externalViewFrameVersion` | Frame counters for the `/mfd.mjpg`, `/stream.mjpg` and `/camview.mjpg` streams respectively - a counter that stops moving while its stream is being watched means that specific capture pipeline has stalled, independent of whether the WebSocket itself is still healthy |
| `mfds` | One entry per MFD part on the craft: `{"part":"MFD1","widgets":[…]}`. Only populated while a client has sent `mfdTabActive` with `on:true` (see below) - walking every MFD's widget tree is real work, skipped otherwise even if the craft has MFDs |
| `cameras` | Names of the craft's camera-vantage parts, for the Cameras tab's picker |

Each widget in `mfds[].widgets` is
`{"name":…, "visible":bool, "position":{"x":…,"y":…}, "pivot":{…}, "size":{…}, "scale":{…}, "rotation":deg, "children":[…], …}`,
plus whichever of these are present depending on what kind of widget it is:
`text`, `fontSize`, `color":[r,g,b]` for a label; `fillAmount`, `fillMethod`,
`fillColor":[r,g,b]` for a gauge; `thickness` for a line. `children` recurses
the same shape for nested widgets.

### `toast`

```json
{"type":"toast","text":"…"}
```

## Client → server commands

JSON text frames on the WebSocket, or the same object POSTed to
`/api/command`. Unknown commands are ignored.

| Command | Effect |
| --- | --- |
| `{"cmd":"throttle","v":0.0–1.0}` | Set throttle |
| `{"cmd":"stage"}` | Activate the next stage |
| `{"cmd":"ag","i":1–10,"on":true}` | Set an activation group |
| `{"cmd":"brake","v":0.0–1.0}` | Apply brakes; send `0` on release |
| `{"cmd":"translation"}` | Toggle RCS translation mode |
| `{"cmd":"warp","d":1}` / `{"d":-1}` | Increase or decrease time warp |
| `{"cmd":"pause"}` | Toggle pause |
| `{"cmd":"lock","mode":"prograde"}` | Navball heading lock: `prograde`, `retrograde`, `target`, `node`, `none` |
| `{"cmd":"mfdClick","part":"MFD1","u":0.0–1.0,"v":0.0–1.0}` | Simulate a tap at normalized image position `(u, v)` (0,0 = top-left) on the named MFD's screen - dispatched through the game's own pointer-event handling, indistinguishable from a real click |
| `{"cmd":"mfdTabActive","on":true}` | Tell the server this connection is (or isn't) looking at the MFD tab, so it can gate the `mfd` message's widget walk |
| `{"cmd":"orbitTabActive","on":true}` | Same idea for the Orbit tab, gating `trajPast`/`trajFuture` sampling |
| `{"cmd":"stopMfdFeed"}` | Force-close any `/mfd.mjpg` connections this session still has open - sent when leaving the MFD tab or switching targets, since some browsers don't reliably close an abandoned MJPEG `<img>` connection on their own |
| `{"cmd":"stopViewFeed"}` | Same idea for `/stream.mjpg` and `/camview.mjpg` |

`mfdTabActive`, `orbitTabActive`, `stopMfdFeed` and `stopViewFeed` only make
sense on the WebSocket (they're tied to a specific connection's state), not as
one-shot `POST /api/command` calls.

Throttle and brake are held for 0.35 s after each message and then released, so
a client that stops sending never leaves an input stuck on, and the keyboard on
the PC keeps working.

## `GET /api/status`

```json
{"mod":"PlusOne","port":8088,"clients":1,"control":true,"video":true,"inFlight":true}
```

`clients` is how many consoles currently have the WebSocket open; `video` is
whether the video feed is enabled in settings (not whether anyone is watching
one); `inFlight` is whether a flight scene is currently loaded at all.
