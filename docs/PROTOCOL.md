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
| `GET /ws` | WebSocket: telemetry down, commands up |
| `GET /stream.mjpg` | `multipart/x-mixed-replace` JPEG stream of the game window |
| `GET /api/status` | Server status as JSON |
| `POST /api/command` | A single command, for clients that would rather not hold a socket open |

## Server → client messages

Sent as JSON text frames on the WebSocket.

### `hello`

Sent once when the socket opens.

```json
{"type":"hello","control":true,"video":{"width":640,"fps":12,"quality":60}}
```

`control` is false when control input is disabled in the settings; `video` is
null when the video feed is disabled.

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
| `airPressure`, `airDensity`, `atmosphereHeight` | Local atmosphere sample |
| `latitude`, `longitude` | Degrees |
| `fuel`, `monoprop`, `battery` | Remaining fractions, 0–1 |
| `mass`, `thrust`, `maxThrust`, `isp`, `twr`, `deltaV`, `burnTime` | Performance |
| `activeEngines`, `activeRcs`, `stage`, `stages` | Counts |
| `groups` | `[{"i":1,"name":"Fairing","on":false}, …]` for the ten activation groups |
| `apoapsis`, `periapsis`, `timeToAp`, `timeToPe`, `eccentricity`, `inclination`, `period` | Orbit; altitudes in metres, inclination in degrees |
| `pitch`, `heading`, `roll`, `aoa` | Attitude, degrees |
| `cf`, `cr`, `cu` | Craft forward/right/up unit vectors, `[x,y,z]` |
| `prograde` | Velocity direction, surface frame inside the atmosphere and orbital frame above it |
| `targetDir` | Unit vector to the navball target, or null |
| `throttle`, `translationMode` | Current control state |

The body-frame vectors are what make navball markers easy: project a direction
onto `cr` and `cu` for screen x and y, and use its dot product with `cf` to tell
whether the marker is in front of or behind the craft.

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

Throttle and brake are held for 0.35 s after each message and then released, so
a client that stops sending never leaves an input stuck on, and the keyboard on
the PC keeps working.
