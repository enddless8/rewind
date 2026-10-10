"""Package the approved raster artwork without redrawing it.

Requires Pillow and NumPy; the source file is always retained unchanged.
"""
import argparse
import hashlib
import io
import json
import struct
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    assets = args.repository / "assets/app"
    evidence = args.repository / "artifacts/app-icon"
    assets.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    image = Image.open(args.source).convert("RGBA")
    assert image.width == image.height, "The approved master must already be square."
    original = np.array(image)
    alpha = original[:, :, 3]
    # The actual tile is one connected component. An alpha >=16 core excludes
    # the input's faint stray exterior speckles; two original pixels of support
    # around that core retain its original antialiased boundary. No new outline,
    # corner geometry, color or alpha is generated inside this support.
    core = Image.fromarray((alpha >= 16).astype("uint8") * 255).copy()
    ImageDraw.floodfill(core, (image.width // 2, image.height // 2), 128)
    tile = Image.fromarray((np.array(core) == 128).astype("uint8") * 255)
    support = np.array(tile.filter(ImageFilter.MaxFilter(5))) != 0
    cleaned = original.copy()
    cleaned[~support] = 0
    master = Image.fromarray(cleaned)
    master.save(assets / "Rewind.png")
    assert np.array_equal(cleaned[support], original[support])
    white = original[:, :, :3].min(axis=2) > 180
    assert np.array_equal(cleaned[white], original[white]), "Approved symbol pixels must be exact."
    assert np.all(cleaned[~support, 3] == 0)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    payloads = []
    frames = []
    for size in sizes:
        # Pillow resizes RGBA in premultiplied-alpha space, avoiding corner halos.
        frame = master.resize((size, size), Image.Resampling.LANCZOS)
        rgba = np.array(frame)
        # Remove only sub-visible Lanczos ringing in the fully transparent field.
        # The smallest frame can otherwise acquire alpha=1 at a distant corner.
        rgba[rgba[:, :, 3] <= 1] = 0
        frame = Image.fromarray(rgba)
        if size == 256:
            png = io.BytesIO()
            frame.save(png, format="PNG")
            payload = png.getvalue()
            encoding = "PNG RGBA"
        else:
            # Standard ICO DIB: bottom-up straight BGRA, doubled height for the
            # XOR bitmap + DWORD-aligned monochrome transparency AND mask.
            pixels = rgba[::-1, :, [2, 1, 0, 3]].tobytes()
            stride = ((size + 31) // 32) * 4
            mask = bytearray(stride * size)
            for y in range(size):
                for x in range(size):
                    if rgba[size - 1 - y, x, 3] == 0:
                        mask[y * stride + x // 8] |= 0x80 >> (x % 8)
            payload = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0) + pixels + mask
            encoding = "DIB32 + AND mask"
        payloads.append(payload)
        frame.save(evidence / f"frame-{size}.png")
        assert rgba[0, 0, 3] == 0 and rgba[size // 2, size // 2, 3] >= 240
        frames.append({"Size": size, "Encoding": encoding, "TransparentPixels": int((rgba[:, :, 3] == 0).sum()),
                       "AntialiasedPixels": int(((rgba[:, :, 3] > 0) & (rgba[:, :, 3] < 240)).sum())})
    offset = 6 + 16 * len(sizes)
    entries = []
    for size, payload in zip(sizes, payloads):
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    ico = struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries) + b"".join(payloads)
    (assets / "Rewind.ico").write_bytes(ico)
    # Decode the finished container, not just the intermediate PNGs.
    reopened = Image.open(assets / "Rewind.ico")
    assert reopened.ico.sizes() == {(n, n) for n in sizes}
    for size in sizes:
        actual = np.array(reopened.ico.getimage((size, size)).convert("RGBA"))
        expected = np.array(Image.open(evidence / f"frame-{size}.png"))
        assert np.array_equal(actual, expected), f"ICO frame {size} must decode sample-exact."
    report = {"Source": str(args.source), "SourceSHA256": hashlib.sha256(args.source.read_bytes()).hexdigest(),
              "Canvas": image.size, "TileCoreBounds": tile.getbbox(), "PreservedOriginalCanvasAndPadding": True,
              "RemovedNonzeroExteriorPixels": int(((alpha > 0) & ~support).sum()),
              "LargestRemovedAlpha": int(alpha[~support].max()), "TileAndSymbolPixelsUnchanged": True,
              "MasterSHA256": hashlib.sha256((assets / "Rewind.png").read_bytes()).hexdigest(), "Frames": frames}
    (evidence / "asset-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
