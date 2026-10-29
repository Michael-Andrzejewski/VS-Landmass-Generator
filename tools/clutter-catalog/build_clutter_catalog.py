"""Catalogue every Vintage Story clutter prop: shape, colour, size, hitbox.

Run from outside the game folder, for example:

    python -I build_clutter_catalog.py
    python -I build_clutter_catalog.py --assets "D:/VS/assets" --out .

It only reads the game assets. It writes clutter-catalog.csv (one row per
placeable type) and clutter-catalog-raw.json (the same data with full
numbers) next to this script, unless --out says otherwise.

How the game treats these blocks (read from the decompiled 1.22.7
VSSurvivalMod.dll, BlockShapeFromAttributes / BlockClutter /
ClutterTypeProps / BEBehaviorShapeFromAttributes):

* Each block (clutter, clutter-devastation, ...) lists "types" in its
  attributes. The block entity's Type picks one. Shape path: the type's
  shapePath, else its code, under "shapes/<shapeBasePath>/".
* Mesh textures: the shape's own "textures" map, overridden by the
  block's attributes.textures, overridden by the type's "textures", and
  finally (only when the block entity has overrideTextureCode set) the
  type's textureFlipCode is replaced by that entry of
  overrideTextureGroups[textureFlipGroupCode].
* Collision boxes: the type's colSelBoxes, rotated with the block
  entity's rotation, then CLAMPED to the 0..1 block, then offset. With no
  colSelBoxes the block-level boxes are used (a full cube for every
  clutter block here). Selection boxes: the type's selBoxes if given,
  else the same as collision. Property names are matched case
  insensitively; other names ("collisionbox", "selectionbox",
  "canStep") are silently ignored by the game.
* A type's "rotation" turns the mesh but NOT the boxes.
"""
import argparse
import colorsys
import csv
import fnmatch
import glob
import json
import math
import os
import re
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np  # noqa: E402
from PIL import Image  # noqa: E402

import vsjson  # noqa: E402

DEFAULT_ASSETS = os.path.join(os.environ.get("APPDATA", ""), "Vintagestory", "assets")
GAME_DOMAIN_DIRS = ("survival", "game", "creative")

# Block classes that use BlockShapeFromAttributes types.
CLUTTER_CLASSES = {
    "BlockClutter": "clutter",
    "ToggleCollisionBox": "clutter",
    "BlockClutterBookshelf": "bookshelf",
    "BlockClutterBookshelfWithLore": "bookshelf",
}

VOX = 16  # voxels per block for the shape-vs-box comparison
EPS = 0.5 / VOX  # a voxel counts as filled if its centre is within half a voxel of an element


# ---------------------------------------------------------------- helpers

def ci(d, key, default=None):
    """Case-insensitive dict lookup, like Newtonsoft's property matching."""
    if not isinstance(d, dict):
        return default
    if key in d:
        return d[key]
    kl = key.lower()
    for k, v in d.items():
        if isinstance(k, str) and k.lower() == kl:
            return v
    return default


def split_domain(path):
    if ":" in path:
        dom, p = path.split(":", 1)
        return dom.lower(), p
    return "game", path


def wildcard_match(pattern, code):
    if ":" in pattern and not pattern.startswith("@"):
        pattern = pattern.split(":", 1)[1]
    if pattern.startswith("@"):
        return re.fullmatch(pattern[1:], code, re.I) is not None
    return fnmatch.fnmatchcase(code.lower(), pattern.lower())


def resolve_bytype(obj, code):
    """Resolve every *ByType key for one block variant code (first match wins)."""
    if isinstance(obj, dict):
        out = {}
        resolved = {}
        for k, v in obj.items():
            if isinstance(k, str) and k.lower().endswith("bytype") and isinstance(v, dict):
                base = k[:-6]
                for pat, val in v.items():
                    if wildcard_match(pat, code):
                        resolved[base] = resolve_bytype(val, code)
                        break
            else:
                out[k] = resolve_bytype(v, code)
        out.update(resolved)
        return out
    if isinstance(obj, list):
        return [resolve_bytype(x, code) for x in obj]
    return obj


class Assets:
    """Finds files of the "game" domain, which is split over several folders."""

    def __init__(self, root):
        self.root = root
        self.dirs = [os.path.join(root, d) for d in GAME_DOMAIN_DIRS
                     if os.path.isdir(os.path.join(root, d))]
        if not self.dirs:
            raise SystemExit("No asset folders under %s" % root)
        self._json = {}

    def find(self, rel):
        rel = rel.replace("\\", "/").lstrip("/")
        for d in self.dirs:
            p = os.path.join(d, *rel.split("/"))
            if os.path.isfile(p):
                return p
        return None

    def glob(self, rel):
        out = []
        for d in self.dirs:
            out += glob.glob(os.path.join(d, *rel.split("/")))
        return sorted(out)

    def rel(self, path):
        for d in self.dirs:
            if path.lower().startswith(d.lower()):
                return os.path.relpath(path, d).replace("\\", "/")
        return path

    def load_json(self, path):
        if path not in self._json:
            self._json[path] = vsjson.load(path)
        return self._json[path]


# ---------------------------------------------------------------- textures

class TexStore:
    """Loads PNGs (with overlays) and answers mean colour of UV regions."""

    def __init__(self, assets):
        self.assets = assets
        self.cache = {}
        self.missing = set()

    def png_path(self, tpath):
        dom, p = split_domain(tpath)
        if dom != "game":
            return None
        if not p.endswith(".png"):
            p = p + ".png"
        if "*" in p:
            hits = self.assets.glob("textures/" + p)
            return hits[0] if hits else None
        return self.assets.find("textures/" + p)

    @staticmethod
    def comp_key(comp):
        ov = tuple(o if isinstance(o, str) else ci(o, "base", "") for o in comp.get("overlays", []))
        return (comp["base"],) + ov

    def get(self, comp):
        """comp: {"base": path, "overlays": [...]} -> texture record or None."""
        key = self.comp_key(comp)
        if key in self.cache:
            return self.cache[key]
        rec = None
        path = self.png_path(comp["base"]) if comp.get("base") else None
        if path is None:
            self.missing.add(comp.get("base"))
        else:
            img = Image.open(path).convert("RGBA")
            for ov in comp.get("overlays", []):
                op = ov if isinstance(ov, str) else ci(ov, "base")
                opath = self.png_path(op) if op else None
                if opath is None:
                    self.missing.add(op)
                    continue
                oimg = Image.open(opath).convert("RGBA")
                if oimg.size != img.size:
                    oimg = oimg.resize(img.size, Image.NEAREST)
                img = Image.alpha_composite(img, oimg)
            a = np.asarray(img).astype(np.float64)
            alpha = a[:, :, 3:4] / 255.0
            pm = np.concatenate([a[:, :, :3] * alpha, alpha], axis=2)  # premultiplied rgb, alpha
            integ = np.zeros((pm.shape[0] + 1, pm.shape[1] + 1, 4))
            integ[1:, 1:] = pm.cumsum(0).cumsum(1)
            rec = {"path": path, "w": img.size[0], "h": img.size[1], "integ": integ,
                   "rel": self.assets.rel(path)}
        self.cache[key] = rec
        return rec

    @staticmethod
    def region(rec, u1, v1, u2, v2):
        """Mean premultiplied rgb and mean alpha over a relative (0..1) UV rectangle."""
        W, H = rec["w"], rec["h"]
        a, b = sorted((u1, u2))
        c, d = sorted((v1, v2))
        x1 = int(math.floor(max(0.0, min(1.0, a)) * W + 1e-6))
        x2 = int(math.ceil(max(0.0, min(1.0, b)) * W - 1e-6))
        y1 = int(math.floor(max(0.0, min(1.0, c)) * H + 1e-6))
        y2 = int(math.ceil(max(0.0, min(1.0, d)) * H - 1e-6))
        x1 = min(max(x1, 0), W - 1)
        y1 = min(max(y1, 0), H - 1)
        x2 = min(max(x2, x1 + 1), W)
        y2 = min(max(y2, y1 + 1), H)
        I = rec["integ"]
        s = I[y2, x2] - I[y1, x2] - I[y2, x1] + I[y1, x1]
        n = (x2 - x1) * (y2 - y1)
        return s / n


def to_comp(v):
    if v is None:
        return None
    if isinstance(v, str):
        return {"base": v, "overlays": []}
    base = ci(v, "base")
    if base is None:
        return None
    ov = list(ci(v, "overlays") or [])
    for bo in ci(v, "blendedOverlays") or []:  # approximated as normal alpha blending
        ov.append(bo if isinstance(bo, str) else ci(bo, "base"))
    return {"base": base, "overlays": [o for o in ov if o]}


# ---------------------------------------------------------------- geometry

def m_translate(x, y, z):
    m = np.eye(4)
    m[:3, 3] = (x, y, z)
    return m


def m_scale(x, y, z):
    return np.diag([x, y, z, 1.0])


def m_rot_xyz(dx, dy, dz):
    """Mat4f.RotateByXYZ: M = Rx @ Ry @ Rz (degrees)."""
    rx, ry, rz = (math.radians(v) for v in (dx, dy, dz))
    cx, sx, cy, sy, cz, sz = math.cos(rx), math.sin(rx), math.cos(ry), math.sin(ry), math.cos(rz), math.sin(rz)
    Rx = np.array([[1, 0, 0, 0], [0, cx, -sx, 0], [0, sx, cx, 0], [0, 0, 0, 1.0]])
    Ry = np.array([[cy, 0, sy, 0], [0, 1, 0, 0], [-sy, 0, cy, 0], [0, 0, 0, 1.0]])
    Rz = np.array([[cz, -sz, 0, 0], [sz, cz, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1.0]])
    return Rx @ Ry @ Rz


def m_about_center(m):
    return m_translate(0.5, 0.5, 0.5) @ m @ m_translate(-0.5, -0.5, -0.5)


def num(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def element_local(e):
    """ShapeElement.GetLocalTransformMatrix (animVersion 0)."""
    o = ci(e, "rotationOrigin") or [0, 0, 0]
    o = [num(x) / 16.0 for x in o]
    f = [num(x) / 16.0 for x in (ci(e, "from") or [0, 0, 0])]
    r = m_rot_xyz(num(ci(e, "rotationX")), num(ci(e, "rotationY")), num(ci(e, "rotationZ")))
    s = m_scale(num(ci(e, "scaleX"), 1.0), num(ci(e, "scaleY"), 1.0), num(ci(e, "scaleZ"), 1.0))
    return m_translate(*o) @ r @ s @ m_translate(f[0] - o[0], f[1] - o[1], f[2] - o[2])


def face_corners(letter, sx, sy, sz):
    if letter == "n":
        return [(0, 0, 0), (sx, 0, 0), (sx, sy, 0), (0, sy, 0)]
    if letter == "s":
        return [(0, 0, sz), (sx, 0, sz), (sx, sy, sz), (0, sy, sz)]
    if letter == "e":
        return [(sx, 0, 0), (sx, 0, sz), (sx, sy, sz), (sx, sy, 0)]
    if letter == "w":
        return [(0, 0, 0), (0, 0, sz), (0, sy, sz), (0, sy, 0)]
    if letter == "u":
        return [(0, sy, 0), (sx, sy, 0), (sx, sy, sz), (0, sy, sz)]
    if letter == "d":
        return [(0, 0, 0), (sx, 0, 0), (sx, 0, sz), (0, 0, sz)]
    return None


class ShapeGeo:
    """Faces (texture code, relative uv, area) and solid elements of one shape."""

    def __init__(self):
        self.faces = []   # (code, (u1,v1,u2,v2) relative, area in block^2, glow, renderpass)
        self.solids = []  # (matrix, size)
        self.corners = []
        self.n_elements = 0
        self.glow_area = 0.0
        self.translucent_area = 0.0


def walk_shape(shape, top_matrix):
    geo = ShapeGeo()
    tw = num(ci(shape, "textureWidth"), 16) or 16
    th = num(ci(shape, "textureHeight"), 16) or 16
    sizes = ci(shape, "textureSizes") or {}

    def rec(elements, parent):
        for e in elements or []:
            if not isinstance(e, dict):
                continue
            geo.n_elements += 1
            M = parent @ element_local(e)
            fr = [num(x) for x in (ci(e, "from") or [0, 0, 0])]
            to = [num(x) for x in (ci(e, "to") or [0, 0, 0])]
            size = [(to[i] - fr[i]) / 16.0 for i in range(3)]
            rp = num(ci(e, "renderPass"), -1)
            any_face = False
            for fkey, f in (ci(e, "faces") or {}).items():
                if not isinstance(f, dict) or not ci(f, "enabled", True):
                    continue
                tex = ci(f, "texture")
                if not tex:
                    continue
                code = tex[1:] if tex.startswith("#") else tex
                cs = face_corners(fkey[:1].lower(), *size)
                if cs is None:
                    continue
                P = (M @ np.array([[x, y, z, 1.0] for x, y, z in cs]).T)[:3].T
                area = float(np.linalg.norm(np.cross(P[1] - P[0], P[3] - P[0])))
                uv = ci(f, "uv")
                if not uv or len(uv) < 4:
                    uv = [0, 0, abs(size[0]) * 16, abs(size[1]) * 16]
                tsz = sizes.get(code) if isinstance(sizes, dict) else None
                w, h = (num(tsz[0], tw), num(tsz[1], th)) if tsz else (tw, th)
                rel = (num(uv[0]) / w, num(uv[1]) / h, num(uv[2]) / w, num(uv[3]) / h)
                glow = num(ci(f, "glow"), 0)
                geo.faces.append((code, rel, area, glow, rp))
                if glow > 0:
                    geo.glow_area += area
                if rp in (2, 3, 4):  # transparent / liquid / translucent passes
                    geo.translucent_area += area
                any_face = True
            if any_face:
                geo.solids.append((M, size))
                for x in (0, size[0]):
                    for y in (0, size[1]):
                        for z in (0, size[2]):
                            geo.corners.append((M @ np.array([x, y, z, 1.0]))[:3])
            rec(ci(e, "children"), M)

    rec(ci(shape, "elements"), top_matrix)
    return geo


def voxelize(solids):
    """Set of (i,j,k) voxel indices (1/16 block) whose centres lie in some element."""
    out = []
    for M, size in solids:
        lo_l = np.array([min(0, s) for s in size]) - EPS
        hi_l = np.array([max(0, s) for s in size]) + EPS
        cs = np.array([[x, y, z, 1.0] for x in (lo_l[0], hi_l[0]) for y in (lo_l[1], hi_l[1])
                       for z in (lo_l[2], hi_l[2])])
        W = (M @ cs.T)[:3].T
        lo = np.floor(W.min(0) * VOX).astype(int)
        hi = np.floor(W.max(0) * VOX).astype(int)
        if np.prod(hi - lo + 1) > 3_000_000:  # absurdly large element; skip
            continue
        ii, jj, kk = np.meshgrid(*[np.arange(lo[a], hi[a] + 1) for a in range(3)], indexing="ij")
        idx = np.stack([ii.ravel(), jj.ravel(), kk.ravel()], 1)
        ctr = (idx + 0.5) / VOX
        try:
            Minv = np.linalg.inv(M)
        except np.linalg.LinAlgError:
            continue
        L = (Minv[:3, :3] @ ctr.T).T + Minv[:3, 3]
        ok = np.all((L >= lo_l) & (L <= hi_l), axis=1)
        if ok.any():
            out.append(idx[ok])
    if not out:
        return np.zeros((0, 3), dtype=int)
    return np.unique(np.concatenate(out), axis=0)


def box_list(raw):
    """Parse colSelBoxes-like JSON into [(x1,y1,z1,x2,y2,z2)]."""
    if raw is None:
        return None
    if isinstance(raw, dict):
        raw = [raw]
    out = []
    for b in raw:
        if isinstance(b, dict):
            out.append(tuple(num(ci(b, k)) for k in ("x1", "y1", "z1", "x2", "y2", "z2")))
    return out


def norm_box(b):
    """Cuboidf.RotatedCopy -> TransformedCopy takes min/max of the corners, so the
    game always ends up with x1<=x2 etc., even at rotation 0."""
    return (min(b[0], b[3]), min(b[1], b[4]), min(b[2], b[5]), max(b[0], b[3]), max(b[1], b[4]), max(b[2], b[5]))


def clamp_box(b):
    return tuple(min(1.0, max(0.0, v)) for v in norm_box(b))


def fmt_box(b):
    return "[%s]" % ",".join(("%.3f" % v).rstrip("0").rstrip(".") if "." in "%.3f" % v else "%.3f" % v
                             for v in b)


def box_voxels(boxes):
    """Voxels (inside 0..15) whose centres are inside any box. Inverted boxes cover nothing."""
    grid = np.zeros((VOX, VOX, VOX), dtype=bool)
    c = (np.arange(VOX) + 0.5) / VOX
    for x1, y1, z1, x2, y2, z2 in boxes:
        gx = (c >= x1) & (c <= x2)
        gy = (c >= y1) & (c <= y2)
        gz = (c >= z1) & (c <= z2)
        grid |= gx[:, None, None] & gy[None, :, None] & gz[None, None, :]
    return grid


def box_volume(b):
    return max(0.0, b[3] - b[0]) * max(0.0, b[4] - b[1]) * max(0.0, b[5] - b[2])


# ---------------------------------------------------------------- colour words

def luma(rgb):
    r, g, b = (v / 255.0 for v in rgb)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def hue_word(rgb):
    r, g, b = (v / 255.0 for v in rgb)
    h, s, v = colorsys.rgb_to_hsv(r, g, b)
    h *= 360
    L = luma(rgb)
    chroma = max(r, g, b) - min(r, g, b)
    if chroma < 0.05 or s < 0.10:
        if L < 0.13:
            return "near-black"
        if L < 0.28:
            return "dark grey"
        if L < 0.42:
            return "mid grey"
        if L < 0.58:
            return "grey"
        if L < 0.78:
            return "pale grey"
        return "off-white"
    if chroma < 0.11 or s < 0.22:
        pre = "dark " if L < 0.28 else ("pale " if L > 0.58 else "")
        if 12 <= h < 70:
            return pre + "grey-brown"
        if 70 <= h < 170:
            return pre + "grey-green"
        if 170 <= h < 265:
            return pre + "blue-grey"
        return pre + "mauve-grey"
    if h < 12 or h >= 340:
        return "dark red" if L < 0.25 else ("pink" if L > 0.6 else "red")
    if h < 45:
        if L < 0.20:
            return "dark brown"
        if s >= 0.45 and L < 0.50 and h < 38:
            return "rust-orange"
        if L < 0.42:
            return "brown"
        if L > 0.62:
            return "tan"
        return "orange" if s > 0.55 else "light brown"
    if h < 70:
        if L < 0.30:
            return "olive-brown"
        if L > 0.62:
            return "beige" if s < 0.45 else "yellow"
        return "ochre" if s > 0.45 else "khaki"
    if h < 140:
        return "dark green" if L < 0.25 else "green"
    if h < 200:
        return "green-copper" if s < 0.65 else "teal"
    if h < 265:
        return "dark blue" if L < 0.25 else "blue"
    return "purple"


# ---------------------------------------------------------------- material / condition

MATERIAL_RULES = [
    ("paper", ["wood/bookshelves/shelf"]),  # shelf faces are painted rows of books
    ("wood", ["/wood/"]),
    ("paper", ["item/lore", "/paper", "book", "scroll", "pages", "writing", "/map", "wallpaper", "paperlabel"]),
    ("glass", ["glass", "luminescent"]),
    ("metal", ["metal", "ingot", "lantern", "nailtexture", "/machine/", "entity/lore/locust", "entity/lore/bell",
               "/iron", "rusty", "tarnished", "corroded", "mesh", "gear", "/chain"]),
    ("bone", ["bone", "skull", "skeleton"]),
    ("cloth", ["cloth", "linen", "leather", "pelt", "rawhide", "burlap", "clothes", "wool", "rope", "/bed/",
               "fabric", "carpet", "tapestry", "banner"]),
    ("ceramic", ["clay", "ceramic", "pot-", "flowerpot", "bloomery", "terracotta", "brick", "pottery", "urn"]),
    ("wood", ["wood", "bark", "plank", "plainoak", "treetrunk", "crate", "barrel", "chest", "cupboard", "table",
              "chair", "shelf", "log", "debarked", "charred", "firewood", "reed"]),
    ("stone", ["stone", "rock", "rubble", "cobble", "gravel", "flint", "/gem", "/ore", "coal", "marble",
               "granite", "basalt", "slate", "/sand", "obsidian"]),
    ("organic", ["plant", "grass", "food", "grain", "creature", "spiderweb", "tallow", "/fat", "resin", "candle",
                 "hay", "straw", "soil", "fire", "flesh", "fungus", "mushroom", "moss", "flower", "leaves",
                 "liquid/", "beeswax", "wax", "root", "seed", "fruit", "vegetable", "honey"]),
]


def classify_texture(rel):
    p = "/" + rel.lower().replace("\\", "/")
    for mat, keys in MATERIAL_RULES:
        if any(k in p for k in keys):
            return mat
    return "other"


NAME_CONDITION = [
    ("pristine", ["pristine"]), ("clean", ["clean"]), ("new", ["-new", "new-"]),
    ("aged", ["aged"]), ("old", ["old"]), ("worn", ["worn"]),
    ("rusty", ["rust"]), ("corroded", ["corrod"]), ("tarnished", ["tarnish"]),
    ("broken", ["broken", "broke", "shatter"]), ("ruined", ["ruin"]), ("damaged", ["damaged", "dmg"]),
    ("burnt", ["burnt", "burned", "charred", "scorch"]), ("rotten", ["rot"]), ("cracked", ["crack"]),
    ("junk", ["junk", "scrap"]), ("destroyed", ["destroy", "collapsed"]), ("mossy", ["moss"]),
    ("dirty", ["dirty"]), ("cobwebbed", ["cobweb"]), ("empty", ["empty"]),
]

TEX_CONDITION = [
    ("rusty", ["rust"]), ("corroded", ["corroded"]), ("tarnished", ["tarnished"]),
    ("burnt", ["charred"]), ("rotten", ["rotten", "veryrotten", "creature/rot"]), ("aged", ["aged"]),
    ("ruined", ["ruined"]), ("mossy", ["mossy"]), ("cracked", ["cracked"]),
    ("clean metal", ["sheet-plain/", "metal/sheet/", "metal/plate/", "/ingot/"]),
]


def name_conditions(code):
    c = code.lower()
    out = []
    for word, keys in NAME_CONDITION:
        if word == "rotten" and ("rotat" in c or "rotor" in c):
            continue
        if word == "old" and ("hold" in c or "fold" in c or "gold" in c):
            continue
        if any(k in c for k in keys):
            out.append(word)
    return out


def texture_conditions(weighted_paths):
    """weighted_paths: {rel path: share}. Conditions covering >= 15% of the model."""
    shares = {}
    for p, w in weighted_paths.items():
        pl = p.lower()
        for word, keys in TEX_CONDITION:
            if any(k in pl for k in keys):
                shares[word] = shares.get(word, 0) + w
                break
    return [w for w, s in sorted(shares.items(), key=lambda x: -x[1]) if s >= 0.15]


# ---------------------------------------------------------------- block loading

def variant_codes(data):
    groups = ci(data, "variantgroups") or []
    combos = [[]]
    for g in groups:
        states = ci(g, "states") or []
        combos = [c + [(ci(g, "code"), s)] for c in combos for s in states]
    base = ci(data, "code")
    out = []
    for c in combos:
        code = base + "".join("-" + s for _, s in c)
        skip = ci(data, "skipVariants") or []
        allow = ci(data, "allowedVariants")
        if any(wildcard_match(p, code) for p in skip):
            continue
        if allow and not any(wildcard_match(p, code) for p in allow):
            continue
        out.append(code)
    return out or [base]


def discover(assets):
    found = []
    for d in assets.dirs:
        for f in sorted(glob.glob(os.path.join(d, "blocktypes", "**", "*.json"), recursive=True)):
            with open(f, "r", encoding="utf-8-sig") as fh:
                text = fh.read()
            if not any(cls in text for cls in CLUTTER_CLASSES):
                continue
            try:
                data = vsjson.loads(text)
            except Exception as ex:  # noqa: BLE001
                print("warn: cannot parse", f, ex, file=sys.stderr)
                continue
            if isinstance(data, dict) and ci(data, "class") in CLUTTER_CLASSES:
                found.append((f, data))
    return found


def clutter_types(blockcode, data, file):
    """Yield type dicts for a BlockClutter-like block variant."""
    attrs = ci(data, "attributes") or {}
    base_path = "shapes/" + str(ci(attrs, "shapeBasePath", "")) + "/"
    types = ci(attrs, "types") or []
    seen = {}
    for t in types:
        if isinstance(t, dict) and ci(t, "code"):
            seen[ci(t, "code")] = t  # later duplicate wins, like clutterByCode
    for code, t in seen.items():
        sp = ci(t, "shapePath")
        if sp is None:
            shape = base_path + code + ".json"
        else:
            if isinstance(sp, dict):
                sp = ci(sp, "base", "")
            dom, p = split_domain(sp)
            if p.startswith("/"):
                p = p if p.startswith("shapes") else "shapes" + p
            elif not p.startswith(base_path):
                p = base_path + p
            shape = p if p.endswith(".json") else p + ".json"
        yield {
            "kind": "clutter", "block": blockcode, "type": code, "variant": "", "file": file,
            "shapes": [(shape, np.eye(4))], "raw": t, "attrs": attrs, "data": data,
        }


def bookshelf_types(blockcode, data, file):
    attrs = ci(data, "attributes") or {}
    base = "shapes/" + str(ci(attrs, "shapeBasePath", "")) + "/"
    back = ci(attrs, "woodbackPanelShapePath")
    back = base + back + ".json" if back else None
    groups = ci(attrs, "variantGroups") or {}
    front = m_translate(0, 0, 0.5)
    behind = m_translate(0, 0, -0.5) @ m_about_center(m_rot_xyz(0, 180, 0))
    for gname, g in groups.items():
        dbl = bool(ci(g, "doubleSided", False))
        for t in ci(g, "types") or []:
            code = ci(t, "code")
            shape = base + code + ".json"
            if gname == "full" and back:
                shapes = [(shape, front), (back, behind)]
            elif dbl:
                shapes = [(shape, front), (shape, behind)]
            else:
                shapes = [(shape, np.eye(4))]
            raw = dict(t)
            raw.setdefault("colSelBoxes", ci(g, "colSelBoxes"))
            if ci(g, "rotation") is not None:
                raw.setdefault("rotation", ci(g, "rotation"))
            raw["randomize"] = False
            yield {
                "kind": "bookshelf", "block": blockcode, "type": code, "variant": gname, "file": file,
                "shapes": shapes, "raw": raw, "attrs": attrs, "data": data, "doublesided": dbl,
            }


# ---------------------------------------------------------------- analysis

def analyse(entry, assets, texs):
    raw, attrs, data = entry["raw"], entry["attrs"], entry["data"]
    notes = []
    rot = ci(raw, "rotation") or {}
    rot_y = num(ci(rot, "y")) if isinstance(rot, dict) else 0.0
    type_m = m_about_center(m_rot_xyz(0, rot_y, 0)) if rot_y else np.eye(4)

    block_tex = {k: to_comp(v) for k, v in (ci(attrs, "textures") or {}).items()}
    type_tex = {k: to_comp(v) for k, v in (ci(raw, "textures") or {}).items()}

    faces = []      # (code, rel uv, area, glow, renderpass, shape-index)
    solids = []
    corners = []
    shape_tex = {}
    shapes_found = []
    n_el = 0
    glow_area = translucent_area = 0.0
    for si, (spath, m) in enumerate(entry["shapes"]):
        p = assets.find(spath)
        if p is None:
            notes.append("shape file missing: " + spath)
            continue
        shape = assets.load_json(p)
        shapes_found.append(spath)
        geo = walk_shape(shape, type_m @ m)
        n_el += geo.n_elements
        glow_area += geo.glow_area
        translucent_area += geo.translucent_area
        for code, rel, area, glow, rp in geo.faces:
            faces.append((code, rel, area, glow, rp, si))
        solids += geo.solids
        corners += geo.corners
        st = {}
        for k, v in (ci(shape, "textures") or {}).items():
            st[k] = to_comp(v)
        shape_tex[si] = st

    def resolve(code, si, flip=None):
        if flip is not None and code == ci(raw, "textureFlipCode"):
            return flip
        if code in type_tex and type_tex[code]:
            return type_tex[code]
        if code in block_tex and block_tex[code]:
            return block_tex[code]
        return shape_tex.get(si, {}).get(code)

    def colour(flip=None):
        acc = np.zeros(4)
        by_path = {}
        missing = set()
        for code, rel, area, glow, rp, si in faces:
            comp = resolve(code, si, flip)
            rec = texs.get(comp) if comp else None
            if rec is None:
                missing.add(code if comp is None else comp["base"])
                continue
            m = TexStore.region(rec, *rel)
            acc += area * m
            by_path[rec["rel"]] = by_path.get(rec["rel"], 0.0) + area * m[3]
        if acc[3] <= 0:
            return None, by_path, missing
        rgb = tuple(int(round(min(255, max(0, v)))) for v in acc[:3] / acc[3])
        return rgb, by_path, missing

    rgb, by_path, missing = colour()
    if missing:
        all_area = sum(f[2] for f in faces) or 1.0
        miss_area = sum(f[2] for f in faces if (resolve(f[0], f[5]) is None or texs.get(resolve(f[0], f[5])) is None))
        notes.append("texture code(s) %s resolve to nothing, so the game draws its unknown texture there "
                     "(%.1f%% of face area)" % (", ".join(sorted(map(str, missing))), 100 * miss_area / all_area))
    total = sum(by_path.values()) or 1.0
    shares = {k: v / total for k, v in by_path.items()}

    mats = {}
    for pth, s in shares.items():
        mats[classify_texture(pth)] = mats.get(classify_texture(pth), 0) + s
    mats_sorted = sorted(mats.items(), key=lambda x: -x[1])
    if not mats_sorted:
        material = "unknown"
    elif mats_sorted[0][1] >= 0.7:
        material = mats_sorted[0][0]
    else:
        material = "mixed"
    material_mix = ", ".join("%s %d%%" % (m, round(s * 100)) for m, s in mats_sorted if s >= 0.03)

    cond = name_conditions(entry["type"])
    tcond = texture_conditions(shares)

    # ---- geometry
    if corners:
        C = np.array(corners)
        bmin, bmax = C.min(0), C.max(0)
    else:
        bmin = bmax = np.zeros(3)
    vox = voxelize(solids)
    inblock = vox[np.all((vox >= 0) & (vox < VOX), axis=1)] if len(vox) else vox
    past = []
    for a, (neg, pos) in enumerate((("west -x", "east +x"), ("down -y", "up +y"), ("north -z", "south +z"))):
        if bmin[a] < -1.0 / 16:
            past.append("%s %.2f" % (neg, -bmin[a]))
        if bmax[a] > 1 + 1.0 / 16:
            past.append("%s %.2f" % (pos, bmax[a] - 1))
    if len(inblock):
        imin = inblock.min(0) / VOX
        imax = (inblock.max(0) + 1) / VOX
        ib_vol = float(np.prod(imax - imin))
    else:
        imin = imax = np.zeros(3)
        ib_vol = 0.0
    fill = len(inblock) / float(VOX ** 3)

    # ---- boxes, exactly as the game resolves them at rotation 0
    colsel_raw = ci(raw, "colSelBoxes")
    sel_raw = ci(raw, "selBoxes")
    ignored = [k for k in raw if k.lower() in ("collisionbox", "selectionbox", "collisionselectionbox",
                                               "collisionboxes", "selectionboxes", "canstep")]
    if ignored:
        notes.append("type keys the game ignores: " + ", ".join(ignored))
    block_box = ci(data, "collisionSelectionBox") or ci(data, "collisionbox")
    if colsel_raw is None:
        if block_box is not None:
            col = [b for b in box_list(block_box)]
            source = "block-level box (no colSelBoxes)"
        else:
            col = [(0, 0, 0, 1, 1, 1)]
            source = "full-cube fallback (no colSelBoxes)"
        col_c = col
    else:
        col = box_list(colsel_raw)
        source = "type colSelBoxes"
        col_c = [clamp_box(b) for b in col]
    if sel_raw is not None:
        sel_c = [clamp_box(b) for b in box_list(sel_raw)]
        sel_source = "type selBoxes"
    else:
        sel_c = col_c
        sel_source = "same as collision"

    flags = []
    inverted = [b for b in col + (box_list(sel_raw) or []) if b[3] < b[0] or b[4] < b[1] or b[5] < b[2]]
    if inverted:
        notes.append("box written inverted in the JSON %s; the game normalises it" %
                     " ".join(fmt_box(b) for b in inverted))
    if any(norm_box(b) != clamp_box(b) for b in col):
        flags.append("box reaches outside the block; game clamps it to one block")
    col_vol = sum(box_volume(b) for b in col_c)
    gridc = box_voxels(col_c)
    model_grid = np.zeros((VOX, VOX, VOX), dtype=bool)
    if len(inblock):
        model_grid[inblock[:, 0], inblock[:, 1], inblock[:, 2]] = True
    n_model = int(model_grid.sum())
    n_box = int(gridc.sum())
    inter = int((model_grid & gridc).sum())
    coverage = inter / n_model if n_model else 0.0
    box_fill = inter / n_box if n_box else 0.0
    gsel = box_voxels(sel_c)
    sel_cov = int((model_grid & gsel).sum()) / n_model if n_model else 0.0

    if not col_c or n_box == 0:
        if n_model > 0.04 * VOX ** 3 and (imax[1] - imin[1]) > 0.2:
            flags.append("no collision on a solid-looking prop (walk through it)")
        else:
            flags.append("no collision")
    if not sel_c or int(gsel.sum()) == 0:
        flags.append("no selection box (cannot be targeted or broken)")
    full_box = col_vol >= 0.9
    total_vol = float(np.prod(np.maximum(bmax - bmin, 0)))
    if source.startswith("full-cube"):
        flags.append("no colSelBoxes: full-cube collision and selection")
    if full_box and total_vol < 0.45 and n_model:
        flags.append("full-block box on a small prop (model bounds are %d%% of a block)" % round(total_vol * 100))
    elif n_model and col_vol > 2.2 * ib_vol and col_vol - ib_vol > 0.12:
        flags.append("box much larger than the model (box %.2f vs model bounds %.2f block^3)" % (col_vol, ib_vol))
    if n_model and n_box and coverage < 0.5:
        flags.append("box covers only %d%% of the model inside the block" % round(coverage * 100))
    if n_model and n_box:
        bc = np.argwhere(gridc).mean(0) / VOX
        mc = np.argwhere(model_grid).mean(0) / VOX
        dist = float(np.linalg.norm(bc - mc))
        if dist > 0.22 and box_fill < 0.35:
            flags.append("box offset from the model (centres %.2f blocks apart)" % dist)
    if past:
        flags.append("model sticks %s past its block; that part has no hitbox" % "/".join(past))
    if rot_y and n_model and coverage < 0.8:
        flags.append("type rotation y=%g turns the model but not the boxes" % rot_y)
    elif rot_y:
        notes.append("type rotation y=%g turns the model but not the boxes; the boxes are already written "
                     "for the turned model" % rot_y)

    # ---- placement guess (rotation 0)
    size = bmax - bmin
    placement = []
    t = 0.07
    sides = []
    if bmin[2] <= t and size[2] <= 0.5:
        sides.append("north (z=0)")
    if bmax[2] >= 1 - t and size[2] <= 0.5:
        sides.append("south (z=1)")
    if bmin[0] <= t and size[0] <= 0.5:
        sides.append("west (x=0)")
    if bmax[0] >= 1 - t and size[0] <= 0.5:
        sides.append("east (x=1)")
    if bmin[1] <= t and 0.9 <= size[1] <= 1.2 and size[0] <= 0.45 and size[2] <= 0.45:
        placement.append("vertical, spans floor to ceiling of its block (stacks into columns)")
    elif bmin[1] <= t:
        if size[1] <= 0.25:
            placement.append("floor scrap (low, %.2f high)" % size[1])
        else:
            placement.append("floor-standing, %.2f high" % size[1])
    elif bmax[1] >= 1 - t and bmin[1] >= 0.3:
        placement.append("ceiling-hung")
    elif len(sides) == 1:
        placement.append("wall-mounted")
    else:
        placement.append("floats in the block (needs support or a wall)")
    if sides:
        placement.append("flat side against " + " and ".join(sides) + " at rotation 0")

    # ---- variants and flips
    variants = []
    if rot_y:
        variants.append("type rotation y %g" % rot_y)
    rnd = ci(raw, "randomize")
    rnd = ci(raw, "randomizeYSize", rnd)
    allow_rnd = bool(ci(attrs, "randomizeDimensions", True))
    if (rnd is None or rnd) and allow_rnd and entry["kind"] == "clutter":
        variants.append("random height 98-102%")
    if ci(raw, "climbable"):
        variants.append("climbable")
    lhsv = ci(raw, "lightHsv")
    if lhsv:
        variants.append("emits light hsv %s" % lhsv)
    if glow_area > 0:
        variants.append("glowing faces")
    if translucent_area > 0.01:
        variants.append("translucent parts")
    sa = ci(raw, "sideAttachable")
    if sa:
        variants.append("attachable sides: " + ",".join(k for k, v in sa.items() if v))
    if entry.get("doublesided"):
        variants.append("double-sided: Type and Type2 halves back to back")
    if entry["variant"] == "full":
        variants.append("full: half shelf plus back panel")

    flip_code = ci(raw, "textureFlipCode")
    flip_group = ci(raw, "textureFlipGroupCode")
    flip_desc = ""
    flip_opts = []
    flip_effect = False
    if flip_code and flip_group:
        groups = ci(attrs, "overrideTextureGroups") or {}
        grp = ci(groups, flip_group)
        used = any(c == flip_code for c, *_ in faces)
        flip_effect = used and isinstance(grp, dict)
        if not isinstance(grp, dict):
            flip_desc = "flip %s via group '%s' (group missing: no effect)" % (flip_code, flip_group)
        elif not used:
            flip_desc = "flip %s via '%s' (shape never uses '%s': no visible effect)" % (flip_code, flip_group, flip_code)
        else:
            share = sum(a for c, _, a, *_ in faces if c == flip_code) / (sum(a for _, _, a, *_ in faces) or 1)
            flip_desc = "flip '%s' (%d%% of faces) via group '%s'" % (flip_code, round(share * 100), flip_group)
            for opt, comp in grp.items():
                c2 = to_comp(comp)
                rgb2, _, _ = colour(flip=c2)
                if rgb2:
                    flip_opts.append((opt, rgb2, luma(rgb2), hue_word(rgb2)))

    # ---- words
    if rgb:
        L = luma(rgb)
        h, s, v = colorsys.rgb_to_hsv(*(x / 255.0 for x in rgb))
        hw = hue_word(rgb)
        hexc = "#%02x%02x%02x" % rgb
    else:
        L = s = 0.0
        hw = "unknown"
        hexc = ""

    fit = rustfall_fit(material, mats, rgb, L, s, hw, cond, entry, flip_opts)
    looks = looks_like(entry["type"], material, hw, L, s, cond, tcond, size, past)

    textures_used = "; ".join("%s %d%%" % (k, round(v * 100)) for k, v in sorted(shares.items(), key=lambda x: -x[1])
                              if v >= 0.02)
    rusty_flip = best_rust_flip(flip_opts)

    be = "Type=%s" % entry["type"]
    if entry["kind"] == "bookshelf":
        be += "; Variant=%s" % entry["variant"] + ("; Type2=<another type of this group>" if entry.get("doublesided") else "")

    return {
        "block": entry["block"],
        "type": entry["type"],
        "variant": entry["variant"],
        "set_on_block_entity": be,
        "shape": " + ".join(s.replace("shapes/", "", 1) for s in shapes_found) or "(missing)",
        "elements": n_el,
        "material": material,
        "material_mix": material_mix,
        "game_material": str(ci(raw, "material") or "Stone (ClutterTypeProps default)"),
        "condition_name": ",".join(cond),
        "condition_textures": ",".join(tcond),
        "color_hex": hexc,
        "brightness": round(L, 3),
        "saturation": round(s, 3),
        "hue": hw,
        "looks_like": looks,
        "size_blocks": "%.2f x %.2f x %.2f" % tuple(size),
        "bbox_min": "%.3f,%.3f,%.3f" % tuple(bmin),
        "bbox_max": "%.3f,%.3f,%.3f" % tuple(bmax),
        "past_block": "/".join(past),
        "placement": "; ".join(placement),
        "model_fill_in_block": round(fill, 3),
        "collision_boxes": " ".join(fmt_box(b) for b in col_c) or "none",
        "collision_source": source,
        "selection_boxes": " ".join(fmt_box(b) for b in sel_c) or "none",
        "selection_source": sel_source,
        "box_covers_model": round(coverage, 2),
        "box_filled_by_model": round(box_fill, 2),
        "selection_covers_model": round(sel_cov, 2),
        "hitbox_flags": "; ".join(flags),
        "variants": "; ".join(variants),
        "texture_flip": flip_desc,
        "flip_options": "; ".join("%s %s %.2f" % (o, "#%02x%02x%02x" % c, l) for o, c, l, _ in flip_opts),
        "best_rust_flip": rusty_flip,
        "rustfall_fit": fit,
        "textures_used": textures_used,
        "notes": "; ".join(notes),
        "_flip_effect": flip_effect,
        "_rgb": rgb,
        "_size": [float(x) for x in size],
        "_bmin": [float(x) for x in bmin],
        "_bmax": [float(x) for x in bmax],
        "_flip_opts": [(o, list(c), round(l, 3), w) for o, c, l, w in flip_opts],
        "_mats": mats,
    }


def is_rusty(rgb, L, s, hw):
    return hw in ("rust-orange", "brown", "dark brown", "orange", "light brown", "grey-brown", "dark grey-brown",
                  "dark red", "red") and L <= 0.45 and s >= 0.2


def best_rust_flip(opts):
    best = None
    for o, c, l, w in opts:
        h, s, v = colorsys.rgb_to_hsv(*(x / 255.0 for x in c))
        if is_rusty(c, l, s, w) or l < 0.30:
            score = l - (0.15 if is_rusty(c, l, s, w) else 0)
            if best is None or score < best[0]:
                best = (score, o, c, l, w)
    if not best:
        return ""
    _, o, c, l, w = best
    return "%s (#%02x%02x%02x, %.2f, %s)" % (o, c[0], c[1], c[2], l, w)


def rustfall_fit(material, mats, rgb, L, s, hw, cond, entry, flip_opts):
    if rgb is None:
        return "unknown (no textures resolved)"
    metal = mats.get("metal", 0)
    if material == "metal" or metal >= 0.6:
        if is_rusty(rgb, L, s, hw):
            f = "good rusty mine metal"
        elif L < 0.30:
            f = "good dark mine metal"
        elif hw in ("green-copper", "teal", "green", "dark green", "grey-green"):
            f = "greenish (verdigris or glowing parts): not mine iron"
        elif L <= 0.40:
            f = "ok: mid-tone worn metal"
        elif s >= 0.45:
            f = "too clean: bright bronze/brass/copper"
        else:
            f = "too pale: %s metal" % (hw if hw.startswith(("light", "pale")) else "light " + hw)
        if not f.startswith("good"):
            bf = best_rust_flip(flip_opts)
            if bf:
                f += "; rusty with overrideTextureCode=" + bf.split(" ")[0]
        return f
    if material == "wood" or mats.get("wood", 0) >= 0.6:
        if "burnt" in cond or hw in ("near-black", "dark grey"):
            return "wood: charred, reads dark"
        return "wood: dark aged timber" if L < 0.3 else "wood: timber prop"
    if material == "stone":
        return "stone: rubble or rock prop"
    if material == "mixed":
        top = sorted(mats.items(), key=lambda x: -x[1])[:2]
        mix = "/".join(m for m, _ in top)
        if metal >= 0.4 and (is_rusty(rgb, L, s, hw) or L < 0.3):
            return "mixed %s: mostly dark/rusty, usable" % mix
        return "mixed %s" % mix
    return {"cloth": "cloth: not mine metal", "paper": "paper: not mine metal",
            "ceramic": "ceramic: domestic", "glass": "glass: lab or lamp",
            "bone": "bone: remains", "organic": "organic"}.get(material, material)


def looks_like(code, material, hw, L, s, cond, tcond, size, past):
    words = [hw, material if material not in ("mixed", "unknown") else "mixed-material"]
    big = max(size) if len(size) else 0
    if big >= 1.5:
        words.append("%.1f blocks long" % big)
    elif big <= 0.35:
        words.append("small")
    txt = " ".join(words)
    agedname = [c for c in cond if c in ("aged", "rusty", "corroded", "ruined", "broken", "junk", "old", "damaged")]
    pale = L > 0.40 and hw not in ("rust-orange", "brown", "dark brown")
    if agedname and pale and material in ("metal", "mixed"):
        txt += ", pale and clean-looking despite '%s' in the name" % agedname[0]
    elif agedname and material == "metal" and not any(t in tcond for t in ("rusty", "corroded", "tarnished", "aged", "burnt")):
        txt += ", plain textures despite '%s'" % agedname[0]
    if tcond:
        txt += "; textures: " + ",".join(tcond)
    if past:
        txt += "; overhangs its block"
    return txt


# ---------------------------------------------------------------- main

FIELDS = ["block", "type", "variant", "set_on_block_entity", "shape", "elements", "material", "material_mix",
          "game_material", "condition_name", "condition_textures", "color_hex", "brightness", "saturation", "hue", "looks_like",
          "size_blocks", "bbox_min", "bbox_max", "past_block", "placement", "model_fill_in_block",
          "collision_boxes", "collision_source", "selection_boxes", "selection_source", "box_covers_model",
          "box_filled_by_model", "selection_covers_model", "hitbox_flags", "variants", "texture_flip",
          "flip_options", "best_rust_flip", "rustfall_fit", "textures_used", "notes"]


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--assets", default=DEFAULT_ASSETS, help="Vintage Story assets folder")
    ap.add_argument("--out", default=os.path.dirname(os.path.abspath(__file__)), help="output folder")
    args = ap.parse_args()
    assets = Assets(args.assets)
    texs = TexStore(assets)
    vfile = glob.glob(os.path.join(args.assets, "version-*.txt"))
    version = os.path.basename(vfile[0])[8:-4] if vfile else "unknown"

    entries = []
    for f, data in discover(assets):
        kind = CLUTTER_CLASSES[ci(data, "class")]
        for code in variant_codes(data):
            d = resolve_bytype(data, code)
            gen = clutter_types if kind == "clutter" else bookshelf_types
            for e in gen(code, d, assets.rel(f)):
                entries.append(e)
    print("game version %s: %d types in %d blocks" % (version, len(entries),
                                                       len(set(e["block"] for e in entries))))
    rows = []
    for i, e in enumerate(entries):
        try:
            rows.append(analyse(e, assets, texs))
        except Exception as ex:  # noqa: BLE001
            print("error on %s %s: %s" % (e["block"], e["type"], ex), file=sys.stderr)
            raise
        if (i + 1) % 200 == 0:
            print("  %d/%d" % (i + 1, len(entries)))
    os.makedirs(args.out, exist_ok=True)
    csv_path = os.path.join(args.out, "clutter-catalog.csv")
    with open(csv_path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=FIELDS, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow(r)
    raw_path = os.path.join(args.out, "clutter-catalog-raw.json")
    with open(raw_path, "w", encoding="utf-8") as fh:
        json.dump({"game_version": version, "rows": rows}, fh, indent=1, default=str)
    counts = {}
    for r in rows:
        counts[r["block"]] = counts.get(r["block"], 0) + 1
    for b, n in counts.items():
        print("  %-36s %d" % (b, n))
    if texs.missing:
        print("missing textures:", ", ".join(sorted(str(m) for m in texs.missing)))
    print("wrote", csv_path)
    print("wrote", raw_path)


if __name__ == "__main__":
    main()
