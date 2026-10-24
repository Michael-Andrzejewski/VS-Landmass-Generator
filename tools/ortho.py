"""Orthographic view of an .lmd dump from one side (looking along an axis).

    python tools/ortho.py <lmd> <out.png> --from s|n|e|w [--half 60] [--depth 60]
                    [--ymin 90] [--ymax 254] [--scale 4] [--only words] [--skip words]
--half: half width of the view across; --depth: how far in front of and
behind the centre plane to look. Centre = dump centre.
"""
import argparse, gzip, json, os, struct
import numpy as np
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument("lmd"); ap.add_argument("out")
ap.add_argument("--from", dest="view", default="s")
ap.add_argument("--half", type=int, default=60); ap.add_argument("--depth", type=int, default=60)
ap.add_argument("--ymin", type=int, default=90); ap.add_argument("--ymax", type=int, default=254)
ap.add_argument("--scale", type=int, default=4)
ap.add_argument("--only", default=""); ap.add_argument("--skip", default="")
ap.add_argument("--dx", type=int, default=0); ap.add_argument("--dz", type=int, default=0)
a = ap.parse_args()

colors = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "viewer", "blockcolors.json"), encoding="utf-8"))
extra = {"rockpolished-andesite": (168, 170, 172), "crackedstonebricks-andesite": (128, 132, 136), "rockpolished-basalt": (62, 62, 66),
         "rockpolished-obsidian": (28, 24, 34), "rock-obsidian": (36, 32, 42), "drock": (92, 46, 40), "packedglacierice": (180, 215, 240),
         "glacierice": (210, 236, 252), "glass-blue": (35, 100, 175), "lava": (255, 90, 20), "devastatedsoil": (110, 60, 50),
         "ironfence": (50, 50, 56), "irondoor": (110, 112, 120), "stonebrickslab": (150, 150, 150), "metalblock-new-plain-steel": (176, 182, 190),
         "metalblock-new-riveted-steel": (150, 156, 166), "metalblock-corroded-riveted": (124, 70, 44), "metalblock-corroded-plain": (104, 66, 48),
         "rock-andesite": (120, 120, 122), "rock-basalt": (70, 70, 74), "ghostlight": (90, 170, 255), "beaconlight": (255, 255, 120),
         "machinecore": (255, 40, 40), "devgrowth": (140, 40, 60), "soil": (120, 90, 60)}

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
sy, sz, sx, oy = h["sy"], h["sz"], h["sx"], h["oy"]
vol = np.repeat(runs["i"], runs["c"]).reshape(sy, sz, sx)
cx, cz = sx // 2 + a.dx, sz // 2 + a.dz


def colour(code):
    c0 = code.split("|")[0].split(":")[-1]
    if c0 == "air": return None
    if a.only and not any(w in code for w in a.only.split(",")): return None
    if a.skip and any(w in code for w in a.skip.split(",")): return None
    if "water" in c0: return (40, 80, 170)
    for k, v in extra.items():
        if c0.startswith(k): return v
    v = colors.get("game:" + c0) or colors.get(code.split("|")[0])
    base = "game:" + c0
    while v is None and "-" in base:
        base = base.rsplit("-", 1)[0]
        v = next((colors[x] for x in colors if x.startswith(base + "-")), None)
    if v is None: return (150, 60, 40) if "clutter" in c0 else (200, 0, 200)
    return tuple(v[:3])


cols = [colour(p) for p in pal]
solid = np.array([c is not None for c in cols])
rgb = np.array([c if c is not None else (0, 0, 0) for c in cols], dtype=np.float32)
y0, y1 = max(0, a.ymin - oy), min(sy, a.ymax - oy + 1)
# build (y, depth, across) with depth 0 = nearest the viewer
if a.view in "sn":
    sub = vol[y0:y1, cz - a.depth:cz + a.depth + 1, cx - a.half:cx + a.half + 1]       # y z x
    if a.view == "s": sub = sub[:, ::-1, :]                                               # nearest = largest z
    else: sub = sub[:, :, ::-1]                                                           # from the north, east is on the left
else:
    sub = vol[y0:y1, cz - a.half:cz + a.half + 1, cx - a.depth:cx + a.depth + 1]         # y z x
    sub = np.transpose(sub, (0, 2, 1))                                                    # y x z
    if a.view == "e": sub = sub[:, ::-1, ::-1]                                            # nearest = largest x; from the east, south is on the left...
s = solid[sub]
hit = s.any(axis=1)
first = np.argmax(s, axis=1)
ny, nd, na = sub.shape
ids = np.take_along_axis(sub, first[:, None, :], axis=1)[:, 0, :]
c = rgb[ids] * (1.0 - 0.45 * (first / max(1, nd - 1)))[..., None]
img = np.full((ny, na, 3), (232, 236, 240), dtype=np.float32)
img[hit] = c[hit]
img = img[::-1].clip(0, 255).astype(np.uint8)
im = Image.fromarray(img).resize((na * a.scale, ny * a.scale), Image.NEAREST)
im.save(a.out)
print(a.out, im.size)
