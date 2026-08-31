"""Drives the baked console in Chromium at iPad size against the simulated flight."""
import os, subprocess, sys, time
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.environ.get("SECOND_SCREEN_BUILD", os.path.join(HERE, "build"))
SHOTS = os.environ.get("SECOND_SCREEN_SHOTS", os.path.join(BUILD, "screenshots"))
os.makedirs(SHOTS, exist_ok=True)
CHROME = os.environ.get("CHROMIUM_PATH", "/opt/pw-browsers/chromium-1194/chrome-linux/chrome")
failures = []

def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ((" :: " + str(detail)) if detail else ""))
    if not ok:
        failures.append(name)

proc = subprocess.Popen(["mono", os.path.join(BUILD, "console-harness.exe")], stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, text=True, bufsize=1)
ready = proc.stdout.readline().strip()
check("harness listening", ready.startswith("READY"), ready)
url = "http://127.0.0.1:18099/"

with sync_playwright() as p:
    browser = p.chromium.launch(executable_path=CHROME,
                                args=["--no-sandbox"])
    # iPad Pro 11" landscape.
    page = browser.new_page(viewport={"width": 1194, "height": 834}, device_scale_factor=2,
                            has_touch=True, is_mobile=False)
    errors = []
    page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(str(e)))

    page.goto(url, wait_until="load")
    page.wait_for_timeout(1500)

    check("no JavaScript errors", not errors, errors)
    check("connection indicator live", "live" in page.get_attribute("#conn", "class"),
          page.get_attribute("#conn", "class"))
    check("craft name from telemetry", page.inner_text("#craftName") == "Ares IV Heavy",
          page.inner_text("#craftName"))
    check("altitude formatted", page.inner_text("#altAsl").endswith((" m", " km")), page.inner_text("#altAsl"))
    check("MET running", page.inner_text("#met").startswith("T+00:0"), page.inner_text("#met"))
    check("activation groups rendered", page.locator("#groupRow .ag").count() == 10,
          page.locator("#groupRow .ag").count())
    check("group names shown", page.locator("#groupRow .ag").first.inner_text().strip().endswith("Fairing"),
          page.locator("#groupRow .ag").first.inner_text())
    check("group state reflected", "on" in (page.locator('[data-group="2"]').get_attribute("class") or ""),
          page.locator('[data-group="2"]').get_attribute("class"))
    check("throttle mirrors the craft", page.inner_text("#throttleValue") == "92%",
          page.inner_text("#throttleValue"))
    check("navball drew something", page.evaluate(
        "() => { const c = document.getElementById('navball');"
        " const d = c.getContext('2d').getImageData(c.width/2, c.height/2, 1, 1).data;"
        " return d[3] > 0; }"))
    page.screenshot(path=os.path.join(SHOTS, "flight.png"))

    # Orbit tab
    page.click('#tabs button[data-tab="orbit"]')
    page.wait_for_timeout(600)
    check("orbit readouts populated", page.inner_text("#apoapsis") != "—", page.inner_text("#apoapsis"))
    check("orbit plot drew something", page.evaluate(
        "() => { const c = document.getElementById('orbitCanvas');"
        " const d = c.getContext('2d').getImageData(c.width/2, c.height/2, 1, 1).data;"
        " return d[3] > 0; }"))
    page.screenshot(path=os.path.join(SHOTS, "orbit.png"))
    page.click('#tabs button[data-tab="flight"]')
    page.wait_for_timeout(200)

    # Controls
    page.locator("#stageButton").dispatch_event("pointerdown", {"pointerId": 1, "isPrimary": True})
    page.wait_for_timeout(150)
    page.locator('[data-group="3"]').dispatch_event("pointerdown", {"pointerId": 1, "isPrimary": True})
    page.wait_for_timeout(150)
    page.locator('[data-cmd="warpUp"]').dispatch_event("pointerdown", {"pointerId": 1, "isPrimary": True})
    page.wait_for_timeout(150)
    page.locator('[data-lock="prograde"]').dispatch_event("pointerdown", {"pointerId": 1, "isPrimary": True})
    page.wait_for_timeout(150)

    box = page.locator("#throttleSlider").bounding_box()
    page.mouse.move(box["x"] + box["width"] / 2, box["y"] + box["height"] * 0.75)
    page.mouse.down()
    page.mouse.move(box["x"] + box["width"] / 2, box["y"] + box["height"] * 0.25, steps=6)
    page.mouse.up()
    page.wait_for_timeout(400)

    # Portrait / narrow layout still renders
    page.set_viewport_size({"width": 834, "height": 1120})
    page.wait_for_timeout(500)
    check("no errors after layout change", not errors, errors)
    check("navball stays square in portrait", page.evaluate(
        "() => { const r = document.getElementById('navball').getBoundingClientRect();"
        " return Math.abs(r.width - r.height) < 2; }"))
    check("telemetry rate shown", page.inner_text("#rate").endswith("Hz"), page.inner_text("#rate"))
    page.screenshot(path=os.path.join(SHOTS, "portrait.png"))
    browser.close()

proc.stdin.write("commands\n")
proc.stdin.flush()
line = proc.stdout.readline().strip()
while not line.startswith("COMMANDS"):
    line = proc.stdout.readline().strip()
got = line[len("COMMANDS "):].split()
check("stage command sent", "stage" in got, got)
check("activation group command sent", "ag" in got, got)
check("time warp command sent", "warp" in got, got)
check("heading lock command sent", "lock" in got, got)
check("throttle drag sent updates", got.count("throttle") >= 2, got)

proc.stdin.write("quit\n")
proc.stdin.flush()
proc.wait(timeout=5)
print()
print("screenshots written to " + SHOTS)
print("FAILURES: %d" % len(failures))
sys.exit(1 if failures else 0)
