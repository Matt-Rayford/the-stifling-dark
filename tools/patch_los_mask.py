#!/usr/bin/env python3
"""Patch obstacles the CV extraction missed into the LoS masks (game-data/maps/*-los-mask.bin).

The original masks were extracted from the board renders by outline color and missed a
few white-outlined vehicles (2026-08 playtest: a Flashlight shone straight through the
Sawmill's blue pickup). Polygons below are hand-traced in TEXTURE pixels of the 4096px
board renders; the tool converts to board-source coordinates, rasterizes, and ORs them
into the mask bits. Idempotent — re-running changes nothing once the bits are set.

Format (see RasterLineOfSightBlocker): "SDLM", int32 w, int32 h, double scale
(mask px -> board px), row-major packed bits, MSB first, 1 = blocks sight.
"""
import struct
import sys

# map id -> (texture width of the measured render, board-source width, polygons)
PATCHES = {
    'sawmill': (4096.0, 7092.0, [
        # The blue pickup between spaces 220/237/255/273, traced just inside its outline.
        [(1840, 3150), (1950, 3095), (2045, 3260), (2122, 3396),
         (2020, 3455), (1928, 3352), (1886, 3252)],
        # The blue container in the Garage beside G-16.
        [(3405, 1885), (3660, 1885), (3660, 2045), (3405, 2045)],
        # The Office's interior wall between O-9's room and O-10, from below O-7's circle
        # down to the south wall (2026-08-28 playtest: a printed sight line lit O-9
        # straight through it).
        [(656, 2340), (690, 2340), (690, 2515), (656, 2515)],
    ]),
}


def point_in_polygon(x, y, polygon):
    inside = False
    n = len(polygon)
    j = n - 1
    for i in range(n):
        xi, yi = polygon[i]
        xj, yj = polygon[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def patch(map_id, texture_width, source_width, polygons):
    path = 'game-data/maps/%s-los-mask.bin' % map_id
    raw = bytearray(open(path, 'rb').read())
    assert raw[:4] == b'SDLM', path
    w, h = struct.unpack_from('<ii', raw, 4)
    scale = struct.unpack_from('<d', raw, 12)[0]
    to_mask = (source_width / texture_width) / scale  # texture px -> mask px
    set_bits = 0
    for polygon in polygons:
        mask_poly = [(x * to_mask, y * to_mask) for x, y in polygon]
        xs = [p[0] for p in mask_poly]
        ys = [p[1] for p in mask_poly]
        for my in range(max(0, int(min(ys))), min(h, int(max(ys)) + 2)):
            for mx in range(max(0, int(min(xs))), min(w, int(max(xs)) + 2)):
                if not point_in_polygon(mx + 0.5, my + 0.5, mask_poly):
                    continue
                index = my * w + mx
                byte = 20 + (index >> 3)
                bit = 0x80 >> (index & 7)
                if not raw[byte] & bit:
                    raw[byte] |= bit
                    set_bits += 1
    open(path, 'wb').write(raw)
    print('%s: %d polygon(s), %d newly blocked mask px' % (map_id, len(polygons), set_bits))


if __name__ == '__main__':
    for map_id, (tex_w, src_w, polys) in PATCHES.items():
        patch(map_id, tex_w, src_w, polys)
    sys.exit(0)
