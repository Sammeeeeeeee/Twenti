"""
Build the application icon from icon.svg.

Renders the SVG at every resolution Windows wants in an .ico file
(16, 24, 32, 48, 64, 128, 256) and combines them into a single
multi-resolution installer/app.ico. Also emits a 256x256 PNG for
the website / store badge use.

The SVG is the Tabler 'hourglass-filled' glyph, originally drawn on a
24x24 viewBox with `fill="currentColor"`. We tint it with the Twenti
accent (#60cdff) and render it on a transparent background so the
.ico looks at home in dark and light tray themes alike.

Run from the repo root:
    pip install pymupdf Pillow
    python tools/build-icon.py
"""
from io import BytesIO
from pathlib import Path

import fitz  # pymupdf
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SVG_SOURCE = ROOT / "icon.svg"
ICO_OUT = ROOT / "installer" / "app.ico"
PNG_OUT = ROOT / "docs" / "icon.png"

ACCENT = "#60cdff"

# Re-tint the SVG: replace currentColor with our accent.
svg_text = SVG_SOURCE.read_text(encoding="utf-8").replace(
    'fill="currentColor"', f'fill="{ACCENT}"'
)

SIZES = [16, 24, 32, 48, 64, 128, 256]


def render(size: int) -> Image.Image:
    """Rasterise the SVG at `size` x `size` pixels onto a transparent canvas."""
    doc = fitz.open(stream=svg_text.encode("utf-8"), filetype="svg")
    page = doc[0]
    # The SVG viewBox is 24x24 — scale to the target pixel size.
    zoom = size / 24.0
    matrix = fitz.Matrix(zoom, zoom)
    pix = page.get_pixmap(matrix=matrix, alpha=True)
    img = Image.frombytes("RGBA", (pix.width, pix.height), pix.samples)
    # PyMuPDF may output slightly off the requested size due to rounding;
    # crop or pad to exactly `size` x `size`.
    if img.size != (size, size):
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        canvas.paste(img, ((size - img.width) // 2, (size - img.height) // 2))
        img = canvas
    return img


def main() -> None:
    ICO_OUT.parent.mkdir(parents=True, exist_ok=True)
    PNG_OUT.parent.mkdir(parents=True, exist_ok=True)

    images = [render(s) for s in SIZES]

    # Pillow saves a multi-resolution ICO when given a list of sizes.
    images[-1].save(
        ICO_OUT,
        format="ICO",
        sizes=[(s, s) for s in SIZES],
    )
    print(f"Wrote {ICO_OUT.relative_to(ROOT)} ({ICO_OUT.stat().st_size:,} bytes)")

    images[-1].save(PNG_OUT, format="PNG")
    print(f"Wrote {PNG_OUT.relative_to(ROOT)} ({PNG_OUT.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
