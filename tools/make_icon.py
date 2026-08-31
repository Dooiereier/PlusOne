#!/usr/bin/env python3
"""Draws web/icon.png, the console's home screen and browser tab icon.

Kept as a script rather than a checked-in binary blob nobody can edit: run it
after changing the colours in web/app.css so the icon still matches the theme.

    python3 tools/make_icon.py
"""

import math
import pathlib
import struct
import zlib

SIZE = 180
OUT = pathlib.Path(__file__).resolve().parent.parent / "web" / "icon.png"

SPACE = (7, 11, 18)
ACCENT = (79, 209, 224)
HULL = (216, 227, 242)
FLAME = (240, 166, 60)


def draw() -> bytearray:
    centre = (SIZE - 1) / 2.0
    rows = bytearray()

    for y in range(SIZE):
        rows.append(0)  # PNG per-row filter: none
        for x in range(SIZE):
            dx, dy = x - centre, y - centre
            radius = math.hypot(dx, dy)

            # Deep space with a soft vignette.
            lift = 1.0 - min(1.0, radius / (SIZE / 2))
            colour = [int(SPACE[0] + 10 * lift), int(SPACE[1] + 16 * lift), int(SPACE[2] + 26 * lift)]

            # Accent ring.
            ring = abs(radius - SIZE * 0.40)
            if ring < 3.0:
                k = 1.0 - ring / 3.0
                colour = [int(colour[i] * (1 - k) + ACCENT[i] * k) for i in range(3)]

            # Rocket body, tapering into a nose cone.
            body_y = dy + SIZE * 0.05
            if -SIZE * 0.26 <= body_y <= SIZE * 0.18 and abs(dx) <= SIZE * 0.070:
                half_width = SIZE * 0.070
                if body_y < -SIZE * 0.10:
                    half_width *= 1 - (body_y + SIZE * 0.10) / (-SIZE * 0.16)
                if abs(dx) <= max(0.0, half_width):
                    colour = list(HULL)

            # Exhaust plume.
            plume = (body_y - SIZE * 0.18) / (SIZE * 0.12)
            if 0 < plume < 1 and abs(dx) < SIZE * 0.045 * (1 - plume):
                colour = list(FLAME)

            rows.extend(colour)

    return rows


def chunk(tag: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))


def main() -> None:
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(bytes(draw()), 9))
           + chunk(b"IEND", b""))
    OUT.write_bytes(png)
    print("wrote {} ({} bytes)".format(OUT, len(png)))


if __name__ == "__main__":
    main()
