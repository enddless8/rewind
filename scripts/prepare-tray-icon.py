"""Prepare the locked tray PNG without redrawing or recoloring its symbol.

Requires Pillow and NumPy. The main application assets are never touched.
"""
import argparse
import hashlib
import json
import math
import struct
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    assets = args.repository / "assets/tray"
    evidence = args.repository / "artifacts/tray-icon"
    assets.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    original = np.array(Image.open(args.source).convert("RGBA"))
    alpha = original[:, :, 3]
    # Retain every visible original pixel and its original antialiasing in a
    # two-pixel support. Only detached near-transparent background remnants
    # with alpha <=3 can be removed; no connected-component guess is needed.
    core = Image.fromarray((alpha >= 4).astype("uint8") * 255)
    support = np.array(core.filter(ImageFilter.MaxFilter(5))) != 0
    assert alpha[~support].max() <= 3, "Do not remove visible approved artwork."
    cleaned = original.copy()
    cleaned[~support] = 0
    assert np.array_equal(cleaned[support], original[support])
    bounds = Image.fromarray(cleaned).getbbox()
    assert bounds is not None
    cropped = Image.fromarray(cleaned).crop(bounds)
    # Balanced square padding of 4% of the longest symbol dimension per side;
    # no symbol resizing, distortion, sharpening or optical redrawing.
    longest = max(cropped.size)
    padding = math.ceil(longest * .04)
    side = longest + 2 * padding
    master = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    position = ((side - cropped.width) // 2, (side - cropped.height) // 2)
    master.paste(cropped, position)
    assert np.array_equal(np.array(master)[position[1]:position[1]+cropped.height,
                                         position[0]:position[0]+cropped.width], np.array(cropped))
    master.save(assets / "RewindTray.png")

    sizes = [16, 20, 24, 32, 40, 48, 64]
    payloads, frames = [], []
    preview = Image.new("RGB", (640, 210), (32, 32, 32))
    draw = ImageDraw.Draw(preview)
    for index, size in enumerate(sizes):
        # Pillow filters RGBA in premultiplied-alpha space to prevent halos.
        rgba = np.array(master.resize((size, size), Image.Resampling.LANCZOS))
        rgba[rgba[:, :, 3] <= 1] = 0
        frame = Image.fromarray(rgba)
        assert rgba[0, 0, 3] == 0
        pixels = rgba[::-1, :, [2, 1, 0, 3]].tobytes()
        stride = ((size + 31) // 32) * 4
        mask = bytearray(stride * size)
        for y in range(size):
            for x in range(size):
                if rgba[size - 1 - y, x, 3] == 0:
                    mask[y * stride + x // 8] |= 0x80 >> (x % 8)
        payloads.append(struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0,
                                    len(pixels), 0, 0, 0, 0) + pixels + mask)
        frame.save(evidence / f"frame-{size}.png")
        preview.paste(frame, (index*90+12, 30), frame)
        zoom = frame.resize((64, 64), Image.Resampling.NEAREST)
        preview.paste(zoom, (index*90+12, 112), zoom)
        draw.text((index*90+12, 10), str(size), fill="white")
        frames.append({"Size": size, "TransparentPixels": int((rgba[:, :, 3] == 0).sum()),
                       "AntialiasedPixels": int(((rgba[:, :, 3] > 0) & (rgba[:, :, 3] < 240)).sum())})
    offset, entries = 6 + 16 * len(sizes), []
    for size, payload in zip(sizes, payloads):
        entries.append(struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    (assets / "RewindTray.ico").write_bytes(struct.pack("<HHH", 0, 1, len(sizes)) +
                                           b"".join(entries) + b"".join(payloads))
    reopened = Image.open(assets / "RewindTray.ico")
    assert reopened.ico.sizes() == {(n, n) for n in sizes}
    for size in sizes:
        assert np.array_equal(np.array(reopened.ico.getimage((size, size)).convert("RGBA")),
                              np.array(Image.open(evidence / f"frame-{size}.png")))
    preview.save(evidence / "preview.png")
    report = {"Source": str(args.source), "SourceSHA256": hashlib.sha256(args.source.read_bytes()).hexdigest(),
              "OriginalCanvas": [original.shape[1], original.shape[0]], "CropBounds": bounds,
              "MasterCanvas": master.size, "SymbolOffset": position, "Padding": padding,
              "SymbolPixelsUnchanged": True, "RemovedExteriorPixels": int(((alpha > 0) & ~support).sum()),
              "LargestRemovedAlpha": int(alpha[~support].max()), "Frames": frames}
    (evidence / "asset-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
