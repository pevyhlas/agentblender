"""Contact-sheet composition with numpy — imaging stays worker-side (D-10: no host imaging dependency).

Grid of labeled cells composed from rendered PNGs; loaded/saved through bpy's image
API (no PIL/ImageMagick on the host or in the worker). Labels use a built-in 5x7
bitmap font, so the sheet needs no font files.
"""

import bpy
import numpy as np

_BG = 0.08          # sheet background gray
_CELL_BG = 0.12     # letterbox gray inside a cell
_INK = 0.92         # label ink
_LABEL_H = 18       # label strip height in px per row
_ADVANCE = 6        # glyph advance in font units (5px glyph + 1px gap)

# 5x7 bitmap font (rows top-down, 5-bit little-endian-on-the-right masks).
# Hand-authored for this project — uppercase, digits, and label punctuation only.
FONT = {
    "0": (0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110),
    "1": (0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110),
    "2": (0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111),
    "3": (0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110),
    "4": (0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010),
    "5": (0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110),
    "6": (0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110),
    "7": (0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000),
    "8": (0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110),
    "9": (0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100),
    "A": (0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001),
    "B": (0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110),
    "C": (0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110),
    "D": (0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100),
    "E": (0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111),
    "F": (0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000),
    "G": (0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111),
    "H": (0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001),
    "I": (0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110),
    "J": (0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100),
    "K": (0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001),
    "L": (0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111),
    "M": (0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001),
    "N": (0b10001, 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001),
    "O": (0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110),
    "P": (0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000),
    "Q": (0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101),
    "R": (0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001),
    "S": (0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110),
    "T": (0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100),
    "U": (0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110),
    "V": (0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100),
    "W": (0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010),
    "X": (0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001),
    "Y": (0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100),
    "Z": (0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111),
    "-": (0b00000, 0b00000, 0b00000, 0b01110, 0b00000, 0b00000, 0b00000),
    "_": (0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111),
    ".": (0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100),
    ":": (0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000),
    "/": (0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000),
    "+": (0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000),
    " ": (0, 0, 0, 0, 0, 0, 0),
}


def _load_png(path):
    """Return a top-down float32 (H, W, 4) array."""
    img = bpy.data.images.load(path)
    w, h = img.size
    buf = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(buf)
    bpy.data.images.remove(img)
    return np.flipud(buf.reshape(h, w, 4))


def _save_png(rgba, path):
    """Save a top-down float32 (H, W, 4) array as PNG via bpy."""
    h, w = rgba.shape[:2]
    img = bpy.data.images.new("abl_sheet", width=w, height=h, alpha=True)
    img.pixels.foreach_set(np.ascontiguousarray(np.flipud(rgba)).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def _fit(src, cell_w, cell_h):
    """Nearest-neighbor fit preserving aspect, letterboxed and alpha-flattened."""
    h, w = src.shape[:2]
    scale = min(cell_w / w, cell_h / h)
    tw, th = max(1, int(w * scale)), max(1, int(h * scale))
    ys = np.clip((np.arange(th) / scale).astype(int), 0, h - 1)
    xs = np.clip((np.arange(tw) / scale).astype(int), 0, w - 1)
    scaled = src[ys][:, xs]

    tile = np.zeros((cell_h, cell_w, 4), dtype=np.float32)
    tile[..., :3] = _CELL_BG
    tile[..., 3] = 1.0
    oy, ox = (cell_h - th) // 2, (cell_w - tw) // 2
    alpha = scaled[..., 3:4]
    region = tile[oy : oy + th, ox : ox + tw]
    region[..., :3] = scaled[..., :3] * alpha + region[..., :3] * (1.0 - alpha)
    return tile


def _draw_text(canvas, text, x, y, scale=2):
    for char in text.upper():
        glyph = FONT.get(char)
        if x + 5 * scale > canvas.shape[1]:
            break
        if glyph is not None:
            for gy, row_bits in enumerate(glyph):
                for gx in range(5):
                    if row_bits & (1 << (4 - gx)):
                        yy, xx = y + gy * scale, x + gx * scale
                        canvas[yy : yy + scale, xx : xx + scale, :3] = _INK
                        canvas[yy : yy + scale, xx : xx + scale, 3] = 1.0
        x += _ADVANCE * scale


def compose(inputs, labels, columns, cell, out):
    """Compose one labeled grid PNG from rendered images; returns the output path."""
    cols = max(1, int(columns))
    rows = (len(inputs) + cols - 1) // cols
    cell = int(cell)
    row_h = cell + _LABEL_H

    canvas = np.zeros((rows * row_h, cols * cell, 4), dtype=np.float32)
    canvas[..., :3] = _BG
    canvas[..., 3] = 1.0

    for i, path in enumerate(inputs):
        r, c = divmod(i, cols)
        y0, x0 = r * row_h, c * cell
        canvas[y0 : y0 + cell, x0 : x0 + cell] = _fit(_load_png(path), cell, cell)
        label = labels[i] if i < len(labels) else ""
        _draw_text(canvas, label, x0 + 4, y0 + cell + 2)

    _save_png(canvas, out)
    return out
