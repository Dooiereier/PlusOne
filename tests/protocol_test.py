"""Exercises the mod's HTTP/WebSocket/MJPEG server against the real socket code."""
import base64, hashlib, json, os, socket, struct, subprocess, sys, time

HOST, PORT = "127.0.0.1", 18088
BUILD = os.environ.get("SECOND_SCREEN_BUILD", os.path.join(os.path.dirname(os.path.abspath(__file__)), "build"))
failures = []

def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ((" :: " + str(detail)) if detail else ""))
    if not ok:
        failures.append(name)

def connect():
    s = socket.create_connection((HOST, PORT), timeout=5)
    s.settimeout(5)
    return s

def read_until(sock, marker):
    buf = b""
    while marker not in buf:
        chunk = sock.recv(4096)
        if not chunk:
            break
        buf += chunk
    return buf

def http_get(sock, path, extra=""):
    sock.sendall(("GET %s HTTP/1.1\r\nHost: x\r\n%s\r\n" % (path, extra)).encode())
    head = read_until(sock, b"\r\n\r\n")
    header_text, _, rest = head.partition(b"\r\n\r\n")
    headers = header_text.decode("latin1").split("\r\n")
    length = 0
    for h in headers[1:]:
        if h.lower().startswith("content-length:"):
            length = int(h.split(":", 1)[1])
    body = rest
    while len(body) < length:
        body += sock.recv(4096)
    return headers[0], headers[1:], body

# ---------------------------------------------------------------- harness boot
proc = subprocess.Popen(["mono", os.path.join(BUILD, "protocol-harness.exe")],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)
json_line = proc.stdout.readline().strip()
parse_ok = proc.stdout.readline().strip()
parse_bad = proc.stdout.readline().strip()
ready = proc.stdout.readline().strip()
check("server starts", ready.startswith("READY"), ready)

payload = json_line[len("JSON "):]
try:
    doc = json.loads(payload)
    check("JsonWriter emits valid JSON", True)
    check("  numbers/nulls/nesting", doc["alt"] == 1234.56789 and doc["nan"] is None
          and doc["cf"] == [1, 0, -1] and doc["targetDir"] is None
          and doc["groups"] == [{"i": 1, "on": True}, {"i": 2, "on": False}]
          and doc["video"] == {"width": 640}, payload)
    check("  string escaping", doc["name"] == 'Fal"con\\9\n', repr(doc["name"]))
except Exception as exc:
    check("JsonWriter emits valid JSON", False, "%s :: %s" % (exc, payload))

check("JsonReader round trip", parse_ok == "PARSE-OK True", parse_ok)
check("JsonReader rejects malformed input", parse_bad == "PARSE-BAD True True", parse_bad)

# ------------------------------------------------------------------ plain HTTP
s = connect()
status, headers, body = http_get(s, "/?t=secret")
check("GET / returns 200", status.startswith("HTTP/1.1 200"), status)
check("  query string parsed", b"hello secret" in body, body)
check("  extra headers sent", any(h.startswith("Set-Cookie:") for h in headers), headers)

status2, _, _ = http_get(s, "/nope")
check("keep-alive serves a second request", status2.startswith("HTTP/1.1 404"), status2)
s.close()

s = connect()
body = b'{"cmd":"throttle","v":0.25}'
s.sendall(b"POST /api/command HTTP/1.1\r\nHost: x\r\nContent-Length: %d\r\n\r\n" % len(body) + body)
head = read_until(s, b"\r\n\r\n")
check("POST answered with 204", head.startswith(b"HTTP/1.1 204"), head[:32])
# The same connection must still work, which proves the body was fully consumed.
status3, _, _ = http_get(s, "/nope")
check("connection reusable after a POST body", status3.startswith("HTTP/1.1 404"), status3)
s.close()

# ------------------------------------------------------------------- WebSocket
def ws_frame(opcode, payload, mask=True, fin=True):
    header = bytes([(0x80 if fin else 0) | opcode])
    n = len(payload)
    if n <= 125:
        header += bytes([(0x80 if mask else 0) | n])
    elif n <= 0xFFFF:
        header += bytes([(0x80 if mask else 0) | 126]) + struct.pack(">H", n)
    else:
        header += bytes([(0x80 if mask else 0) | 127]) + struct.pack(">Q", n)
    if not mask:
        return header + payload
    key = os.urandom(4)
    masked = bytes(b ^ key[i & 3] for i, b in enumerate(payload))
    return header + key + masked

class WsClient:
    def __init__(self, sock):
        self.sock = sock
        self.buf = b""

    def _need(self, n):
        while len(self.buf) < n:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise EOFError
            self.buf += chunk

    def read_frame(self):
        self._need(2)
        b0, b1 = self.buf[0], self.buf[1]
        fin, opcode, masked, n = b0 & 0x80, b0 & 0x0F, b1 & 0x80, b1 & 0x7F
        offset = 2
        if n == 126:
            self._need(4); n = struct.unpack(">H", self.buf[2:4])[0]; offset = 4
        elif n == 127:
            self._need(10); n = struct.unpack(">Q", self.buf[2:10])[0]; offset = 10
        self._need(offset + n)
        payload = self.buf[offset:offset + n]
        self.buf = self.buf[offset + n:]
        return fin, opcode, masked, payload

key = base64.b64encode(os.urandom(16)).decode()
s = connect()
s.sendall(("GET /ws HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
           "Sec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n" % key).encode())
head = read_until(s, b"\r\n\r\n")
expected = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
check("WebSocket handshake status", b"101 Switching Protocols" in head, head[:40])
check("  Sec-WebSocket-Accept correct", expected.encode() in head, head)

ws = WsClient(s)
ws.buf = head.split(b"\r\n\r\n", 1)[1]
fin, opcode, masked, payload = ws.read_frame()
hello = json.loads(payload)
check("server sends hello frame", opcode == 1 and fin and not masked and hello["type"] == "hello", payload)

seen = []
for _ in range(3):
    _, _, _, payload = ws.read_frame()
    seen.append(json.loads(payload))
check("server pushes telemetry frames", all(m["type"] == "telemetry" for m in seen)
      and [m["n"] for m in seen] == [0, 1, 2], seen)
check("  non-ASCII survives the wire", seen[0]["text"] == 'quote" backslash\\ unicodeé', seen[0]["text"])

# client -> server: a normal message, a fragmented one, and a big one
s.sendall(ws_frame(0x1, b'{"cmd":"throttle","v":0.75}'))
s.sendall(ws_frame(0x1, b'{"cmd":"st', fin=False) + ws_frame(0x0, b'age","v":1}'))
s.sendall(ws_frame(0x1, b'{"cmd":"pad","v":2,"junk":"' + b"x" * 400 + b'"}'))
s.sendall(ws_frame(0x9, b"ping-payload"))

deadline = time.time() + 3
pong = None
while time.time() < deadline and pong is None:
    _, opcode, _, payload = ws.read_frame()
    if opcode == 0xA:
        pong = payload
check("server answers ping with pong", pong == b"ping-payload", pong)

time.sleep(0.3)
proc.stdin.write("received\n")
proc.stdin.flush()
line = proc.stdout.readline().strip()
while not line.startswith("RECEIVED"):
    line = proc.stdout.readline().strip()
got = line[len("RECEIVED "):].split("|")
check("POST command reached the handler", 'post:{"cmd":"throttle","v":0.25}' in got, got)
check("WebSocket message parsed", "ws:throttle=0.75" in got, got)
check("fragmented message reassembled", "ws:stage=1" in got, got)
check("126-byte-length frame decoded", "ws:pad=2" in got, got)
s.close()

# ------------------------------------------------------------------------ MJPEG
s = connect()
s.sendall(b"GET /stream.mjpg HTTP/1.1\r\nHost: x\r\n\r\n")
data = b""
deadline = time.time() + 5
while time.time() < deadline:
    try:
        chunk = s.recv(4096)
    except socket.timeout:
        break
    if not chunk:
        break
    data += chunk
    if data.count(b"--frame") >= 5:
        break
check("MJPEG content type", b"multipart/x-mixed-replace; boundary=frame" in data, data[:120])
parts = data.split(b"\r\n--frame\r\n")[1:]
ok_parts = [p for p in parts if b"Content-Type: image/jpeg" in p and b"\xff\xd8" in p]
check("MJPEG frames are well formed", len(ok_parts) >= 4, len(ok_parts))
s.close()

proc.stdin.write("quit\n")
proc.stdin.flush()
proc.wait(timeout=5)

print()
print("FAILURES: %d" % len(failures))
sys.exit(1 if failures else 0)
