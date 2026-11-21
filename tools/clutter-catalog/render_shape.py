"""Render any block or item shape file to a PNG, from the game's or a mod's assets.

    python -I render_shape.py out.png <shape.json> [--tex code=domain:path ...] [--mod <assets folder> ...]
           [--size 320] [--cam 1,0.85,1.35] [--exposure 1.3] [--grid N]

A shape path may be a file path, or "domain:block/..." (looked up under the
game's or a --mod assets folder). Texture codes the shape leaves open can be
filled with --tex, the way a blocktype's "textures" does. --grid draws the
block cells the model spans (default on, 0 to hide).
"""

import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np  # noqa: E402
from PIL import Image  # noqa: E402

import build_clutter_catalog as B  # noqa: E402
import render_clutter as R  # noqa: E402
import vsjson  # noqa: E402


class Domains:
    """Asset lookup across the game's folders and mod asset folders (<assets>/<domain>/...)."""

    def __init__(self, game_root, mods):
        self.game = B.Assets(game_root)
        self.mods = {}
        for m in mods:
            for d in os.listdir(m):
                if os.path.isdir(os.path.join(m, d)):
                    self.mods.setdefault(d, []).append(os.path.join(m, d))

    def find(self, domain, rel):
        if domain == "game":
            return self.game.find(rel)
        for root in self.mods.get(domain, []):
            p = os.path.join(root, *rel.split("/"))
            if os.path.isfile(p):
                return p
        return None


def load_tex(dom, code, cache):
    if code in cache:
        return cache[code]
    d, p = B.split_domain(code) if ":" in code else ("game", code)
    path = dom.find(d, "textures/" + (p if p.endswith(".png") else p + ".png"))
    arr = np.asarray(Image.open(path).convert("RGBA")).astype(np.float32) / 255.0 if path else None
    cache[code] = arr
    return arr


def shape_quads(dom, shape, texmap, place=np.eye(4), cache=None, glow_boost=True):
    cache = {} if cache is None else cache
    tw = B.num(B.ci(shape, "textureWidth"), 16) or 16
    th = B.num(B.ci(shape, "textureHeight"), 16) or 16
    sizes = B.ci(shape, "textureSizes") or {}
    tex = dict((k, v if isinstance(v, str) else B.ci(v, "base")) for k, v in (B.ci(shape, "textures") or {}).items())
    tex.update(texmap)
    quads = []

    def rec(elements, parent):
        for e in elements or []:
            M = parent @ B.element_local(e)
            fr = [B.num(x) for x in (B.ci(e, "from") or [0, 0, 0])]
            to = [B.num(x) for x in (B.ci(e, "to") or [0, 0, 0])]
            size = [(to[i] - fr[i]) / 16.0 for i in range(3)]
            for fkey, f in (B.ci(e, "faces") or {}).items():
                if not isinstance(f, dict) or not B.ci(f, "enabled", True):
                    continue
                t = B.ci(f, "texture")
                if not t:
                    continue
                code = t[1:] if t.startswith("#") else t
                letter = fkey[:1].lower()
                cs = B.face_corners(letter, *size)
                if cs is None:
                    continue
                P = (M @ np.array([[x, y, z, 1.0] for x, y, z in cs]).T)[:3].T
                uv = B.ci(f, "uv")
                if not uv or len(uv) < 4:
                    uv = [0, 0, abs(size[0 if letter in "nsud" else 2]) * 16, abs(size[2 if letter in "ud" else 1]) * 16]
                tsz = sizes.get(code) if isinstance(sizes, dict) else None
                w, h = (B.num(tsz[0], tw), B.num(tsz[1], th)) if tsz else (tw, th)
                rel = (B.num(uv[0]) / w, B.num(uv[1]) / h, B.num(uv[2]) / w, B.num(uv[3]) / h)
                img = load_tex(dom, tex[code], cache) if tex.get(code) else None
                if img is not None and glow_boost and B.num(B.ci(f, "glow"), 0) > 0:
                    img = img.copy()
                    g = min(1.0, B.num(B.ci(f, "glow"), 0) / 255.0)
                    img[..., :3] = np.clip(img[..., :3] * (1 + 0.9 * g) + 0.15 * g, 0, 1)
                quads.append((P, np.array(R.face_uv_corners(letter, rel, B.ci(f, "rotation", 0))), img, letter))
            rec(B.ci(e, "children"), M)

    rec(B.ci(shape, "elements"), place)
    return quads


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("shape")
    ap.add_argument("--tex", action="append", default=[])
    ap.add_argument("--mod", action="append", default=[])
    ap.add_argument("--size", type=int, default=320)
    ap.add_argument("--cam", default="1,0.85,1.35")
    ap.add_argument("--exposure", type=float, default=1.3)
    ap.add_argument("--grid", type=int, default=1)
    a = ap.parse_args()
    dom = Domains(B.DEFAULT_ASSETS, a.mod)
    if os.path.isfile(a.shape):
        path = a.shape
    else:
        d, p = B.split_domain(a.shape) if ":" in a.shape else ("game", a.shape)
        path = dom.find(d, "shapes/" + (p if p.endswith(".json") else p + ".json"))
    shape = vsjson.load(path)
    texmap = dict(t.split("=", 1) for t in a.tex)
    quads = shape_quads(dom, shape, texmap)
    pts = np.concatenate([q[0] for q in quads])
    lo, hi = np.floor(pts.min(0) + 1e-3), np.ceil(pts.max(0) - 1e-3)
    lines = []
    if a.grid:
        for x in range(int(lo[0]), int(hi[0])):
            for y in range(int(lo[1]), int(hi[1])):
                for z in range(int(lo[2]), int(hi[2])):
                    for e in R.box_edges((x, y, z, x + 1, y + 1, z + 1)):
                        lines.append((e, (120, 120, 128), 1))
    V = R.view_matrix(tuple(float(v) for v in a.cam.split(",")))
    img = R.raster(quads, lines, size=a.size, V=V, bounds_pts=pts, bg=(46, 48, 54, 255), exposure=a.exposure)
    img.convert("RGB").save(a.out)
    print("wrote", a.out)


if __name__ == "__main__":
    main()
