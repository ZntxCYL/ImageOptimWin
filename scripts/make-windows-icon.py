#!/usr/bin/env python3
"""Regenerate a Windows ICO with explicit transparency masks for all small sizes.

Source is the already transparent, user-supplied-ImageOptim-derived AppIcon.png.
This script requires Pillow to run on a developer computer; build and CI consume
the checked-in ICO and do not require Pillow.
"""
from io import BytesIO
from pathlib import Path
import struct
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / 'src/ImageOptim.Win/Assets/AppIcon.png'
DEST = ROOT / 'src/ImageOptim.Win/Assets/AppIcon.ico'
SIZES = (16, 24, 32, 48, 64, 128, 256)


def dib_icon(image):
    width, height = image.size
    rgba = image.convert('RGBA')
    xor = rgba.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes('raw', 'BGRA')
    stride = ((width + 31) // 32) * 4
    alpha = rgba.getchannel('A')
    and_rows = bytearray()
    for y in range(height - 1, -1, -1):
        row = bytearray(stride)
        for x in range(width):
            if alpha.getpixel((x, y)) < 16:
                row[x // 8] |= 0x80 >> (x % 8)
        and_rows.extend(row)
    header = struct.pack('<IiiHHIIiiII', 40, width, height * 2,
                         1, 32, 0, len(xor) + len(and_rows), 0, 0, 0, 0)
    return header + xor + bytes(and_rows)


def main():
    source = Image.open(SOURCE).convert('RGBA')
    if source.size != (512, 512):
        raise SystemExit('Expected a 512x512 transparent RGBA app icon')
    if any(source.getpixel(p)[3] for p in
           ((0, 0), (511, 0), (0, 511), (511, 511))):
        raise SystemExit('App icon source has an opaque corner/background')
    payloads = []
    for size in SIZES:
        small = source.resize((size, size), Image.Resampling.LANCZOS)
        if size == 256:
            stream = BytesIO()
            small.save(stream, 'PNG')
            payload = stream.getvalue()
        else:
            payload = dib_icon(small)
        payloads.append(payload)
    header_length = 6 + 16 * len(SIZES)
    offset = header_length
    directory = bytearray(struct.pack('<HHH', 0, 1, len(SIZES)))
    for size, payload in zip(SIZES, payloads):
        directory.extend(struct.pack('<BBBBHHII',
                                     size if size < 256 else 0,
                                     size if size < 256 else 0,
                                     0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    DEST.write_bytes(bytes(directory) + b''.join(payloads))
    check = Image.open(DEST)
    if set(check.ico.sizes()) != {(n, n) for n in SIZES}:
        raise SystemExit('Generated ICO lacks a required frame')
    for dimensions in check.ico.sizes():
        image = check.ico.getimage(dimensions).convert('RGBA')
        for x, y in ((0,0),(image.width-1,0),(0,image.height-1),
                     (image.width-1,image.height-1)):
            if image.getpixel((x,y))[3] != 0:
                raise SystemExit(f'Icon {dimensions} has an opaque corner')
    print(f'[PASS] Windows ICO with alpha and explicit AND masks: {DEST}')


if __name__ == '__main__':
    main()
