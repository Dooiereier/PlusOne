# Changelog

## v0.1.0

First release. Turns an iPad — or any browser on your network — into a touch
flight console for Juno: New Origins, served by a small web server running
inside the game. Nothing is installed on the tablet.

**Status:** the mod compiles against the game's own assemblies and its transport
and console layers are covered by automated tests, but it has not yet been
loaded by Juno itself. Treat this as a first cut and expect rough edges in game.

### Console

- **Flight** — navball with prograde/retrograde/target markers and a heading
  tape, altitude ASL/AGL, surface/orbital/vertical/horizontal speed, g-force,
  Mach, apoapsis and periapsis with time to each, TWR, stage ΔV, thrust, mass,
  Isp, remaining burn time, engine and stage counts, air pressure and density,
  latitude/longitude, and fuel, monopropellant and battery bars.
- **Orbit** — a live orbit plot with the planet, the edge of the atmosphere,
  apoapsis and periapsis markers and your position on the conic, alongside the
  orbital elements.
- **View** — an optional MJPEG stream of the game window, off until a client
  asks for it.
- **Controls** — throttle slider, stage button, all ten activation groups with
  their in-game names, RCS translation, brake, time warp, pause, and navball
  heading locks.
- Responsive down to phone width; add it to the iPad home screen for a
  full-screen console.

### Mod

- Settings page for port, access token, control input, telemetry rate and video
  size, frame rate and quality. Changes apply within a second, no restart.
- Access is gated by a token that persists across launches, so a home screen
  shortcut keeps working. Control input can be disabled for a read-only console.
- The connection address is written to the log and shown when a flight starts.
- Throttle and brake are released shortly after the tablet stops sending, so the
  keyboard on the PC keeps working and no input is ever left stuck on.
- Video capture allocates nothing and captures nothing while no one is watching.

### Known limitations

- This is a companion console, not an operating system second display. See the
  README for the Sidecar/Duet route if you want a genuine extended desktop.
- Attitude control (pitch, roll, yaw) from the tablet is not implemented; the
  transport supports it, the console has no joystick yet.
- Only the active craft is reported.
