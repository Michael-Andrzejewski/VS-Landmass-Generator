"""Pictures of the tin mine's pipes next to rusty pipe candidates.

Each set is placed the way the mine places props: against a wall of rusted
plate (metalblock-corroded-*-rusty-iron) with basalt floor and ceiling around
it. Every set gets two pictures: as it looks, and with its hitboxes drawn on
(red = collision, which is also what you click unless a yellow selection box
is drawn). The grey frames are the block cells the props sit in.

Writes <repo>/export/pipe-compare/*.png and index.html.
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np  # noqa: E402
from PIL import Image  # noqa: E402

import build_clutter_catalog as B  # noqa: E402
import render_clutter as R  # noqa: E402

OUT = os.path.join(R.REPO, "export", "pipe-compare")

WALL_X = -1          # the tunnel wall: the column of blocks at x = -1
EXPOSURE = 1.6     # every picture brightened the same, so dark rust on rust can be judged
PUSH = 0.34          # the mine pushes wall pipes this far toward the rock


def cube_quads(imgs, x, y, z, tex, faces="nsewud"):
    comp = {"base": tex, "overlays": []}
    img = imgs.get(comp)
    out = []
    full = (0.0, 0.0, 1.0, 1.0)
    for f in faces:
        cs = B.face_corners(f, 1, 1, 1)
        P = np.array(cs, float) + (x, y, z)
        out.append((P, np.array(R.face_uv_corners(f, full, 0)), img, f))
    return out


def backdrop(imgs, zs, ys, ceiling_y=None, floor=True):
    q = []
    for z in zs:
        for y in ys:
            plate = abs(z) <= 1 and y <= ys[0] + 3      # the mine plates 3 wide, floor to head height
            q += cube_quads(imgs, WALL_X, y, z, "block/metal/tarnished/rusty-iron" if plate else "block/stone/rock/basalt3", "e")
        if floor:
            q += cube_quads(imgs, WALL_X + 1, ys[0] - 1, z, "block/stone/rock/basalt1", "u")
        if ceiling_y is not None:
            for x in (WALL_X + 1, WALL_X + 2):
                q += cube_quads(imgs, x, ceiling_y, z, "block/stone/rock/basalt2", "d")
    return q


def scene(items, imgs, assets, back, boxes, size=480):
    """items as in R.render_scene; back: extra quads drawn as scenery."""
    quads, lines, cells = list(back), [], []
    for it in items:
        entry, (dx, dy, dz), q, flip, off = it
        place = B.m_translate(dx + off[0], dy + off[1], dz + off[2]) @ B.m_about_center(B.m_rot_xyz(0, 90 * q, 0))
        quads += R.collect_quads(entry, assets, imgs, place, flip)
        cells.append((dx, dy, dz))
        if boxes:
            col, sel = R.boxes_of(entry, place)
            turn = B.m_about_center(B.m_rot_xyz(0, 90 * q, 0))
            for group, rgb, wd in ((col, (255, 50, 50), 2), (sel or [], (255, 215, 40), 1)):
                for b in group:
                    cs = np.array([(x, y, z, 1.0) for x in (b[0], b[3]) for y in (b[1], b[4]) for z in (b[2], b[5])])
                    t = (turn @ cs.T)[:3].T
                    lo, hi = np.clip(t.min(0), 0, 1), np.clip(t.max(0), 0, 1)
                    rb = (lo[0] + dx + off[0], lo[1] + dy + off[1], lo[2] + dz + off[2],
                          hi[0] + dx + off[0], hi[1] + dy + off[1], hi[2] + dz + off[2])
                    for e in R.box_edges(rb):
                        lines.append((e, rgb, wd))
    cl = []
    if boxes:
        for (dx, dy, dz) in cells:
            for e in R.box_edges((dx, dy, dz, dx + 1, dy + 1, dz + 1)):
                cl.append((e, (200, 200, 200), 1))
    bpts = np.concatenate([q_[0] for q_ in quads])
    return R.raster(quads, cl + lines, size=size, bounds_pts=bpts, bg=(58, 60, 66, 255), exposure=EXPOSURE)


def main():
    assets = B.Assets(B.DEFAULT_ASSETS)
    imgs = R.ImgStore(assets)
    entries = {(e["block"], e["type"]): e for e in R.load_entries(assets)}
    cat = {(r["block"], r["type"]): r for r in json.load(open(os.path.join(HERE, "clutter-catalog-raw.json"), encoding="utf-8"))["rows"]}

    def E(block, t):
        return entries[(block, t)]

    C, D = "clutter", "clutter-devastation"
    wall_push = (-PUSH, 0, 0)      # toward the wall at x = -1
    # quarter turns: the mine's own rot for "wall to the west" is +90 degrees
    sets = []

    # 1. what the mine places today (0.71.0 on): a straight column, 4 high
    sets.append(dict(
        key="now-column", title="Now: rusty pipe column up the wall",
        status="current",
        types=[(C, "pipe-veryrusted-v-empty")],
        items=[(E(C, "pipe-veryrusted-v-empty"), (0, y, 0), 0, None, wall_push) for y in range(4)],
        back=dict(zs=(-2, -1, 0, 1, 2), ys=range(0, 4), ceil=None),
        place="Since 0.71.0. One per wall machine, beside it, floor to head height (up to 4 blocks), pushed 0.34 toward the rock. Both ends are cut flat: it starts on the floor and stops in mid air."))

    # 2. what it placed before 0.71.0
    sets.append(dict(
        key="old-ceiling", title="Before 0.71.0: tarnished pipes on the ceiling",
        status="old",
        types=[(D, "pipelong4-aged"), (D, "pipelong3-aged")],
        items=[(E(D, "pipelong4-aged"), (0, 3, -2), 0, None, (0, 0, 0))],
        back=dict(zs=(-2, -1, 0, 1, 2), ys=range(0, 4), ceil=None),
        place="The pipes you said did not fit. Pale khaki tin-bronze, one block entity drawing a 4-block pipe."))

    # candidates
    sets.append(dict(
        key="A", title="A: the same column, finished at both ends",
        status="candidate",
        types=[(C, "pipe-veryrusted-v-W"), (C, "pipe-veryrusted-v-empty"), (C, "pipe-veryrusted-v-empty_broken"), (C, "pipe-veryrusted-v-topW")],
        items=[(E(C, "pipe-veryrusted-v-W"), (0, 0, 0), 0, None, wall_push),
               (E(C, "pipe-veryrusted-v-empty"), (0, 1, 0), 0, None, wall_push),
               (E(C, "pipe-veryrusted-v-empty_broken"), (0, 2, 0), 0, None, wall_push),
               (E(C, "pipe-veryrusted-v-topW"), (0, 3, 0), 0, None, wall_push)],
        back=dict(zs=(-2, -1, 0, 1, 2), ys=range(0, 4), ceil=None),
        place="Same spot and push as now. The bottom piece has a stub that goes into the wall, the top piece bends into the wall, and one middle piece is the cracked variant. Reads as a pipe that comes out of the rock and goes back in."))

    sets.append(dict(
        key="B", title="B: a rusty pipe run along the ceiling",
        status="candidate",
        types=[(C, "pipe-veryrusted-h-EW"), (C, "pipe-veryrusted-h-EW_broken"), (C, "pipe-veryrusted-h-broken1"), (C, "pipe-veryrusted-v-topW")],
        items=[(E(C, "pipe-veryrusted-h-broken1"), (0, 3, -2), 1, None, (-0.25, 0.25, 0)),
               (E(C, "pipe-veryrusted-h-EW"), (0, 3, -1), 1, None, (-0.25, 0.25, 0)),
               (E(C, "pipe-veryrusted-h-EW_broken"), (0, 3, 0), 1, None, (-0.25, 0.25, 0)),
               (E(C, "pipe-veryrusted-h-EW"), (0, 3, 1), 1, None, (-0.25, 0.25, 0)),
               (E(C, "pipe-veryrusted-h-EW"), (0, 3, 2), 1, None, (-0.25, 0.25, 0))],
        back=dict(zs=(-2, -1, 0, 1, 2), ys=range(0, 4), ceil=None),
        place="Brings back the ceiling pipes you had before, now rusty iron. One block per segment, each with its own hitbox, so a 5-block run has a hitbox the whole way. A broken end where the run stops."))

    sets.append(dict(
        key="C", title="C: the old long pipes with the rust texture flip",
        status="candidate",
        types=[(D, "pipelong4"), (D, "pipelong4bent")],
        items=[(E(D, "pipelong4"), (0, 3, -2), 0, "rustysheet", (0, 0, 0))],
        back=dict(zs=(-2, -1, 0, 1, 2), ys=range(0, 4), ceil=None),
        place="Same model as before 0.71.0, recoloured with overrideTextureCode=rustysheet."))

    sets.append(dict(
        key="D", title="D: devastation junk pipes",
        status="candidate",
        types=[(D, "junkpipe3"), (D, "junkpipe4")],
        items=[(E(D, "junkpipe3"), (0, 0, -1), 1, None, (0, 0, 0)),
               (E(D, "junkpipe4"), (0, 0, 1), 1, None, (0, 0, 0))],
        back=dict(zs=(-2, -1, 0, 1, 2), ys=range(0, 3), ceil=None),
        place="Bent and broken pipe pieces from the devastation set, standing against the wall."))

    sets.append(dict(
        key="E", title="E: Jonas lamp pipe in the column (lit)",
        status="candidate",
        types=[("jonas-lamp", "lightsource/on/lumi-pipe-v-lamp")],
        items=[(E(C, "pipe-veryrusted-v-W"), (0, 0, 0), 0, None, wall_push),
               (E(C, "pipe-veryrusted-v-empty"), (0, 1, 0), 0, None, wall_push),
               (E("jonas-lamp", "lightsource/on/lumi-pipe-v-lamp"), (0, 2, 0), 0, None, wall_push),
               (E(C, "pipe-veryrusted-v-topW"), (0, 3, 0), 0, None, wall_push)],
        back=dict(zs=(-2, -1, 0, 1, 2), ys=range(0, 4), ceil=None),
        place="Set A with one segment swapped for a lit Jonas lamp pipe. Same width and hitbox as the plain pipe. An add-on to A, not a replacement on its own."))

    os.makedirs(OUT, exist_ok=True)
    data = []
    for s in sets:
        b = s["back"]
        back = backdrop(imgs, b["zs"], list(b["ys"]), b["ceil"])
        a = scene(s["items"], imgs, assets, back, boxes=False)
        h = scene(s["items"], imgs, assets, back, boxes=True)
        a.convert("RGB").save(os.path.join(OUT, s["key"] + "-look.png"))
        h.convert("RGB").save(os.path.join(OUT, s["key"] + "-hitbox.png"))
        singles = []
        for blk, t in s["types"]:
            e = entries[(blk, t)]
            img = R.render_scene([(e, (0, 0, 0), 0, None)], assets, imgs, size=200, exposure=EXPOSURE)
            bg = Image.new("RGBA", img.size, (58, 60, 66, 255))
            bg.alpha_composite(img)
            fn = s["key"] + "-single-" + t.replace("/", "_") + ".png"
            bg.convert("RGB").save(os.path.join(OUT, fn))
            r = cat[(blk, t)]
            singles.append(dict(file=fn, block=blk, type=t, colour=r["color_hex"], brightness=r["brightness"],
                                size=r["size_blocks"], past=r["past_block"], col=r["collision_boxes"],
                                cover=r["box_covers_model"], filled=r["box_filled_by_model"],
                                flags=r["hitbox_flags"], fit=r["rustfall_fit"], flip=r["best_rust_flip"]))
        data.append(dict(key=s["key"], title=s["title"], status=s["status"], place=s["place"], singles=singles))
    with open(os.path.join(OUT, "sets.json"), "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=1)
    print("wrote", OUT)


if __name__ == "__main__":
    main()
