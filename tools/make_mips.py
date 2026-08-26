#!/usr/bin/env python3
"""Pack sharp Lanczos mip pyramids for card-like art (Lemonade Wars' technique).

Unity auto-generates mipmaps with a plain box filter, which turns minified board
text to mush. This packs proper Lanczos downscales instead: for every PNG in the
directory given as argv[1], each mip level (Unity's dims: width>>L x height>>L,
stopping below 24px) is resized from the ORIGINAL with Lanczos and stacked
top-to-bottom into a sibling `<name>.mips.png`. TokenArt.LoadTextureSharp splits
the stack back into the texture's mip levels (the layout math there mirrors this).

Run by tools/sync_unity.sh after rendering the player boards.
"""

import pathlib
import sys

from PIL import Image

MIN_DIM = 24

root = pathlib.Path(sys.argv[1])
packed = 0
for path in sorted(root.glob("*.png")):
    if ".mips" in path.name:
        continue
    out = path.with_name(path.stem + ".mips.png")
    if out.exists() and out.stat().st_mtime >= path.stat().st_mtime:
        continue
    image = Image.open(path).convert("RGBA")
    w, h = image.size
    levels = []
    level = 1
    while (w >> level) >= MIN_DIM and (h >> level) >= MIN_DIM:
        levels.append(image.resize((w >> level, h >> level), Image.LANCZOS))
        level += 1
    if not levels:
        continue
    sheet = Image.new("RGBA", (levels[0].width, sum(l.height for l in levels)))
    y = 0
    for l in levels:
        sheet.paste(l, (0, y))
        y += l.height
    sheet.save(out)
    packed += 1
print(f"  packed {packed} mip pyramid(s) in {root}")
