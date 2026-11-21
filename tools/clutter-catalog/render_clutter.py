"""Render pictures of every clutter type, with its hitboxes drawn on.

Uses the shape walk and texture lookup of build_clutter_catalog.py, then draws
the textured faces with a small software rasteriser (no game client needed).

Each picture shows:
- the model, lit like the game does (top faces bright, sides darker),
- a thin grey outline of the block the prop sits in,
- the collision boxes in red (the selection boxes in yellow when they differ).

Usage (from any folder outside the game):
    python -I render_clutter.py                 # every type -> <out>/<block>/<type>.png
    python -I render_clutter.py --only pipe     # types whose name contains "pipe"
    python -I render_clutter.py --sheets        # also contact sheets, 48 per page

Default output: <repo>/export/clutter-renders (export/ is git-ignored).
Importable: render_scene() draws several placed props together (a pipe run).
"""

import argparse
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np  # noqa: E402
from PIL import Image, ImageDraw, ImageFont  # noqa: E402

import build_clutter_catalog as B  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_OUT = os.path.join(REPO, "export", "clutter-renders")

# face shading the game uses for block faces (up, down, north/south, east/west)
SHADE = {"u": 1.0, "d": 0.5, "n": 0.8, "s": 0.8, "e": 0.6, "w": 0.6}


# ---------------------------------------------------------------- loading

def load_entries(assets):
    entries = []
    for f, data in B.discover(assets):
        kind = B.CLUTTER_CLASSES[B.ci(data, "class")]
        for code in B.variant_codes(data):
            d = B.resolve_bytype(data, code)
            gen = B.clutter_types if kind == "clutter" else B.bookshelf_types
            for e in gen(code, d, assets.rel(f)):
                entries.append(e)
    return entries


class ImgStore:
    """Composited RGBA textures as float arrays."""

    def __init__(self, assets):
        self.assets = assets
        self.cache = {}

    def get(self, comp):
        if not comp or not comp.get("base"):
            return None
        key = B.TexStore.comp_key(comp)
        if key in self.cache:
            return self.cache[key]
        ts = B.TexStore(self.assets)
        path = ts.png_path(comp["base"])
        arr = None
        if path:
            img = Image.open(path).convert("RGBA")
            for ov in comp.get("overlays", []):
                op = ov if isinstance(ov, str) else B.ci(ov, "base")
                opath = ts.png_path(op) if op else None
                if opath:
                    oimg = Image.open(opath).convert("RGBA")
                    if oimg.size != img.size:
                        oimg = oimg.resize(img.size, Image.NEAREST)
                    img = Image.alpha_composite(img, oimg)
            arr = np.asarray(img).astype(np.float32) / 255.0
        self.cache[key] = arr
        return arr


# ---------------------------------------------------------------- geometry

def face_uv_corners(letter, uv, rot):
    """UVs for the 4 corners from B.face_corners, in the game's convention."""
    u1, v1, u2, v2 = uv
    # corner order from face_corners: see there. Map left/right, top/bottom.
    if letter == "n":   # seen from -z: left is +x
        c = [(u2, v2), (u1, v2), (u1, v1), (u2, v1)]
    elif letter == "s":  # seen from +z: left is -x
        c = [(u1, v2), (u2, v2), (u2, v1), (u1, v1)]
    elif letter == "e":  # seen from +x: left is +z
        c = [(u2, v2), (u1, v2), (u1, v1), (u2, v1)]
    elif letter == "w":  # seen from -x: left is -z
        c = [(u1, v2), (u2, v2), (u2, v1), (u1, v1)]
    elif letter == "u":  # from above: u along x, v along z
        c = [(u1, v1), (u2, v1), (u2, v2), (u1, v2)]
    else:               # from below
        c = [(u1, v2), (u2, v2), (u2, v1), (u1, v1)]
    k = int(round(B.num(rot) / 90.0)) % 4
    if k:
        c = c[k:] + c[:k]
    return c


def collect_quads(entry, assets, imgs, place=np.eye(4), flip=None):
    """Textured quads of one placed prop: (P[4,3], UV[4,2], tex, shade)."""
    raw, attrs = entry["raw"], entry["attrs"]
    rot = B.ci(raw, "rotation") or {}
    rot_y = B.num(B.ci(rot, "y")) if isinstance(rot, dict) else 0.0
    type_m = B.m_about_center(B.m_rot_xyz(0, rot_y, 0)) if rot_y else np.eye(4)
    block_tex = {k: B.to_comp(v) for k, v in (B.ci(attrs, "textures") or {}).items()}
    type_tex = {k: B.to_comp(v) for k, v in (B.ci(raw, "textures") or {}).items()}
    flip_comp = None
    if flip:
        grp = B.ci(B.ci(attrs, "overrideTextureGroups") or {}, B.ci(raw, "textureFlipGroupCode") or "")
        if isinstance(grp, dict) and B.ci(grp, flip) is not None:
            flip_comp = B.to_comp(B.ci(grp, flip))
    flip_code = B.ci(raw, "textureFlipCode")
    quads = []
    for spath, m in entry["shapes"]:
        p = assets.find(spath)
        if p is None:
            continue
        shape = assets.load_json(p)
        shape_tex = {k: B.to_comp(v) for k, v in (B.ci(shape, "textures") or {}).items()}
        tw = B.num(B.ci(shape, "textureWidth"), 16) or 16
        th = B.num(B.ci(shape, "textureHeight"), 16) or 16
        sizes = B.ci(shape, "textureSizes") or {}

        def resolve(code):
            if flip_comp is not None and code == flip_code:
                return flip_comp
            for d in (type_tex, block_tex, shape_tex):
                if d.get(code):
                    return d[code]
            return None

        def rec(elements, parent):
            for e in elements or []:
                if not isinstance(e, dict):
                    continue
                M = parent @ B.element_local(e)
                fr = [B.num(x) for x in (B.ci(e, "from") or [0, 0, 0])]
                to = [B.num(x) for x in (B.ci(e, "to") or [0, 0, 0])]
                size = [(to[i] - fr[i]) / 16.0 for i in range(3)]
                for fkey, f in (B.ci(e, "faces") or {}).items():
                    if not isinstance(f, dict) or not B.ci(f, "enabled", True):
                        continue
                    tex = B.ci(f, "texture")
                    if not tex:
                        continue
                    code = tex[1:] if tex.startswith("#") else tex
                    letter = fkey[:1].lower()
                    cs = B.face_corners(letter, *size)
                    if cs is None:
                        continue
                    P = (M @ np.array([[x, y, z, 1.0] for x, y, z in cs]).T)[:3].T
                    uv = B.ci(f, "uv")
                    if not uv or len(uv) < 4:
                        uv = [0, 0, abs(size[0 if letter in "nsud" else 2]) * 16,
                              abs(size[2 if letter in "ud" else 1]) * 16]
                    tsz = sizes.get(code) if isinstance(sizes, dict) else None
                    w, h = (B.num(tsz[0], tw), B.num(tsz[1], th)) if tsz else (tw, th)
                    rel = (B.num(uv[0]) / w, B.num(uv[1]) / h, B.num(uv[2]) / w, B.num(uv[3]) / h)
                    img = imgs.get(resolve(code))
                    UV = np.array(face_uv_corners(letter, rel, B.ci(f, "rotation", 0)))
                    quads.append((P, UV, img, letter))
                rec(B.ci(e, "children"), M)

        rec(B.ci(shape, "elements"), place @ type_m @ m)
    return quads


def boxes_of(entry, place):
    """Collision and selection boxes after placement, as world corner sets."""
    raw = entry["raw"]

    def norm(lst):
        out = []
        for b in B.box_list(lst) or []:
            out.append(B.clamp_box(B.norm_box(b)))
        return out

    col = norm(B.ci(raw, "colSelBoxes") or B.ci(raw, "collisionSelectionBoxes"))
    if not col:
        col = [(0, 0, 0, 1, 1, 1)]
    sel = norm(B.ci(raw, "selBoxes")) or None
    return col, sel


# ---------------------------------------------------------------- camera

def view_matrix(cam=(1.0, 0.85, 1.35)):
    """Rows: screen right, screen up, toward the camera. Default camera: south-east, above."""
    b = np.array(cam, float)
    b /= np.linalg.norm(b)
    r = np.cross(np.array([0.0, 1.0, 0.0]), b)
    r /= np.linalg.norm(r)
    u = np.cross(b, r)
    return np.array([r, u, b])


def project(V, pts):
    q = pts @ V.T
    return q[:, 0], -q[:, 1], q[:, 2]   # screen x right, screen y down, depth (bigger = nearer)


# ---------------------------------------------------------------- raster

def raster(quads, lines, size=256, ss=2, V=None, bounds_pts=None, bg=(0, 0, 0, 0), exposure=1.0):
    V = view_matrix() if V is None else V
    W = size * ss
    allp = np.concatenate([q[0] for q in quads] + ([bounds_pts] if bounds_pts is not None else []))
    sx, sy, _ = project(V, allp)
    pad = 0.06
    span = max(sx.max() - sx.min(), sy.max() - sy.min()) * (1 + 2 * pad) or 1
    scale = W / span
    ox = (sx.min() + sx.max()) / 2
    oy = (sy.min() + sy.max()) / 2

    def to_px(pts):
        x, y, d = project(V, pts)
        return (x - ox) * scale + W / 2, (y - oy) * scale + W / 2, d

    color = np.zeros((W, W, 4), np.float32)
    color[...] = np.array(bg, np.float32) / 255.0
    depth = np.full((W, W), -1e9, np.float32)
    for P, UV, tex, letter in quads:
        x, y, d = to_px(P)
        sh = SHADE.get(letter, 0.8)
        # shade by the turned normal too (props can be rotated)
        n = np.cross(P[1] - P[0], P[3] - P[0])
        nl = np.linalg.norm(n)
        if nl > 1e-9:
            n = n / nl
            sh = 0.5 + 0.5 * abs(n[1]) if n[1] > 0 else 0.5 + 0.1 * abs(n[1])
            sh = max(sh, 0.6 + 0.2 * abs(n[2]) if n[1] <= 0.5 else sh)
            if n[1] > 0.5:
                sh = 1.0
            elif n[1] < -0.5:
                sh = 0.5
            else:
                sh = 0.8 if abs(n[2]) >= abs(n[0]) else 0.6
        for tri in ((0, 1, 2), (0, 2, 3)):
            tx, ty, td = x[list(tri)], y[list(tri)], d[list(tri)]
            tuv = UV[list(tri)]
            x0, x1 = int(max(0, math.floor(tx.min()))), int(min(W - 1, math.ceil(tx.max())))
            y0, y1 = int(max(0, math.floor(ty.min()))), int(min(W - 1, math.ceil(ty.max())))
            if x1 < x0 or y1 < y0:
                continue
            den = (ty[1] - ty[2]) * (tx[0] - tx[2]) + (tx[2] - tx[1]) * (ty[0] - ty[2])
            if abs(den) < 1e-9:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            a = ((ty[1] - ty[2]) * (gx - tx[2]) + (tx[2] - tx[1]) * (gy - ty[2])) / den
            b = ((ty[2] - ty[0]) * (gx - tx[2]) + (tx[0] - tx[2]) * (gy - ty[2])) / den
            c = 1 - a - b
            inside = (a >= -1e-4) & (b >= -1e-4) & (c >= -1e-4)
            if not inside.any():
                continue
            z = a * td[0] + b * td[1] + c * td[2]
            sub = depth[y0:y1 + 1, x0:x1 + 1]
            ok = inside & (z > sub)
            if not ok.any():
                continue
            u = a * tuv[0, 0] + b * tuv[1, 0] + c * tuv[2, 0]
            v = a * tuv[0, 1] + b * tuv[1, 1] + c * tuv[2, 1]
            if tex is None:
                rgba = np.zeros(ok.shape + (4,), np.float32)
                rgba[...] = (1.0, 0.0, 1.0, 1.0)      # the game's unknown texture is magenta
            else:
                th_, tw_ = tex.shape[:2]
                iu = np.clip(np.floor((u % 1.0 if False else u) * tw_), 0, tw_ - 1).astype(int)
                iv = np.clip(np.floor(v * th_), 0, th_ - 1).astype(int)
                rgba = tex[iv, iu]
            ok = ok & (rgba[..., 3] > 0.5)
            if not ok.any():
                continue
            sub[ok] = z[ok]
            csub = color[y0:y1 + 1, x0:x1 + 1]
            csub[ok, :3] = rgba[ok, :3] * sh * exposure
            csub[ok, 3] = 1.0
    img = Image.fromarray((np.clip(color, 0, 1) * 255).astype(np.uint8), "RGBA")
    draw = ImageDraw.Draw(img)
    for pts, rgb, wd in lines:
        x, y, _ = to_px(np.array(pts))
        draw.line(list(zip(x, y)), fill=rgb, width=wd * ss)
    return img.resize((size, size), Image.LANCZOS)


def box_edges(b):
    x1, y1, z1, x2, y2, z2 = b
    c = [(x, y, z) for x in (x1, x2) for y in (y1, y2) for z in (z1, z2)]
    out = []
    for i in range(8):
        for j in range(i + 1, 8):
            diff = sum(1 for k in range(3) if c[i][k] != c[j][k])
            if diff == 1:
                out.append([c[i], c[j]])
    return out


def rot_box_y(b, quarter):
    """Turn a box about the block centre by quarter*90 degrees, like the game's rotate step."""
    x1, y1, z1, x2, y2, z2 = b
    for _ in range(quarter % 4):
        x1, z1, x2, z2 = 1 - z2, x1, 1 - z1, x2
    return (min(x1, x2), y1, min(z1, z2), max(x1, x2), y2, max(z1, z2))


# ---------------------------------------------------------------- scenes

def render_scene(items, assets, imgs, size=320, show_boxes=True, show_cells=True, V=None, exposure=1.0):
    """items: list of (entry, (dx,dy,dz), quarter_turns, flip, (ox,oy,oz)).

    The game turns a prop by rotateY about its block centre and moves the box
    with offsetX/Y/Z, so the drawing does the same.
    """
    quads, lines, cells = [], [], []
    for it in items:
        entry, (dx, dy, dz), q, flip = it[0], it[1], it[2], it[3]
        off = it[4] if len(it) > 4 else (0, 0, 0)
        place = (B.m_translate(dx + off[0], dy + off[1], dz + off[2])
                 @ B.m_about_center(B.m_rot_xyz(0, 90 * q, 0)))
        quads += collect_quads(entry, assets, imgs, place, flip)
        cells.append((dx, dy, dz))
        if show_boxes:
            col, sel = boxes_of(entry, place)
            turn = B.m_about_center(B.m_rot_xyz(0, 90 * q, 0))
            for group, rgb, wd in ((col, (230, 40, 40), 2), (sel or [], (240, 200, 40), 1)):
                for b in group:
                    cs = np.array([(x, y, z, 1.0) for x in (b[0], b[3]) for y in (b[1], b[4]) for z in (b[2], b[5])])
                    t = (turn @ cs.T)[:3].T
                    lo, hi = np.clip(t.min(0), 0, 1), np.clip(t.max(0), 0, 1)   # clamped to the block
                    rb = (lo[0] + dx + off[0], lo[1] + dy + off[1], lo[2] + dz + off[2],
                          hi[0] + dx + off[0], hi[1] + dy + off[1], hi[2] + dz + off[2])
                    for e in box_edges(rb):
                        lines.append((e, rgb, wd))
    cell_lines = []
    if show_cells:
        for (dx, dy, dz) in cells:
            for e in box_edges((dx, dy, dz, dx + 1, dy + 1, dz + 1)):
                cell_lines.append((e, (150, 150, 150), 1))
    bpts = np.array([p for (dx, dy, dz) in cells for p in
                     [(dx + a, dy + b, dz + c) for a in (0, 1) for b in (0, 1) for c in (0, 1)]], float)
    if not quads:
        quads = [(bpts[:4], np.zeros((4, 2)), None, "u")]
    return raster(quads, cell_lines + lines, size=size, V=V, bounds_pts=bpts, exposure=exposure)


def label(img, text, sub=None):
    W, H = img.size
    out = Image.new("RGBA", (W, H + 34), (32, 32, 36, 255))
    out.paste(img, (0, 0), img)
    d = ImageDraw.Draw(out)
    try:
        f = ImageFont.truetype("arial.ttf", 12)
    except OSError:
        f = ImageFont.load_default()
    d.text((4, H + 2), text, fill=(235, 235, 235), font=f)
    if sub:
        d.text((4, H + 18), sub, fill=(170, 170, 170), font=f)
    return out


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--assets", default=B.DEFAULT_ASSETS)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--only", default=None, help="only types whose code contains this")
    ap.add_argument("--size", type=int, default=192)
    ap.add_argument("--sheets", action="store_true", help="also write contact sheets")
    args = ap.parse_args()
    assets = B.Assets(args.assets)
    imgs = ImgStore(assets)
    entries = load_entries(assets)
    if args.only:
        entries = [e for e in entries if args.only.lower() in e["type"].lower()]
    print("%d types" % len(entries))
    done = []
    for i, e in enumerate(entries):
        name = e["type"].replace("/", "__") + ("." + e["variant"] if e["variant"] else "")
        folder = os.path.join(args.out, e["block"])
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, name + ".png")
        try:
            img = render_scene([(e, (0, 0, 0), 0, None)], assets, imgs, size=args.size)
        except Exception as ex:  # noqa: BLE001
            print("error on %s %s: %s" % (e["block"], e["type"], ex), file=sys.stderr)
            continue
        img.save(path)
        done.append((e, path))
        if (i + 1) % 100 == 0:
            print("  %d/%d" % (i + 1, len(entries)))
    if args.sheets:
        per = 48
        cols = 8
        for s in range(0, len(done), per):
            chunk = done[s:s + per]
            tiles = [label(Image.open(p).convert("RGBA"), e["type"][:30], e["block"]) for e, p in chunk]
            tw, th = tiles[0].size
            rows = (len(tiles) + cols - 1) // cols
            sheet = Image.new("RGBA", (cols * tw, rows * th), (24, 24, 28, 255))
            for k, t in enumerate(tiles):
                sheet.paste(t, ((k % cols) * tw, (k // cols) * th))
            sp = os.path.join(args.out, "sheet-%03d.png" % (s // per + 1))
            sheet.convert("RGB").save(sp)
        print("wrote %d sheets" % ((len(done) + per - 1) // per))
    print("wrote %d pictures to %s" % (len(done), args.out))


if __name__ == "__main__":
    main()
