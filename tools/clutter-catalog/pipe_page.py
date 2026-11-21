"""Build export/pipe-compare/index.html from pipe_compare.py's pictures and notes."""

import base64
import html
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(os.path.dirname(HERE)), "export", "pipe-compare")

VERDICT = {
    "now-column": ("Current", "Good hitbox, plain look",
                   "Each segment's hitbox is a 0.375 wide square column that wraps the pipe exactly (100% of the model inside it). "
                   "The look is the weak part: a straight rod that starts on the floor and stops in mid air, rust coloured on rust plate, so it nearly disappears."),
    "old-ceiling": ("Before 0.71.0", "Wrong colour, hitbox on the first block only",
                    "Khaki tarnished tin-bronze (brightness 0.42 to 0.46; rusty iron is about 0.19). "
                    "One block entity draws a 3 or 4 block pipe. Only its own block has a hitbox, so the other 2 or 3 blocks of pipe cannot be hit or clicked."),
    "A": ("Recommended", "Same hitboxes as now, better look",
          "Every piece has its own snug hitbox (100% of the model inside its box). The stub at the bottom and the bend at the top go into the wall. "
          "Their boxes move with the 0.34 push, so the part past the cell sits inside the rock, out of reach. "
          "Code change: the column loop picks v-W for the bottom, v-topW for the top and sometimes v-empty_broken in the middle, turned so the stub faces the wall."),
    "B": ("Good add-on", "Hitbox the whole way along",
          "Each 1-block segment has a box 0.31 tall and 0.375 wide around the pipe (83% of the model inside; the flanges poke out a little). "
          "Unlike the old long pipes, a 5-block run has a hitbox along all 5 blocks. "
          "Code change: Clut() needs an offsetY so the pipe can sit against the ceiling. Without it the pipe hangs at mid-block height."),
    "C": ("Not recommended", "Same hitbox problem as before",
          "The flip turns the pipe brown (brightness 0.24) but the brackets keep their pale texture, because a flip changes one texture only. "
          "The hitbox is still on the first block only."),
    "D": ("Not recommended for pipes", "Hitbox far bigger than the pipe",
          "Rusty enough (0.19), but each box is a half-block slab across the whole cell: an invisible table around a thin bent pipe. "
          "junkpipe3 covers 56% of its model, junkpipe4 63%, and junkpipe3 sticks 0.09 out of its cell."),
    "E": ("Optional extra", "Same hitbox as the plain pipe",
          "The lit Jonas pipe lamp has the same 0.375 column box as v-empty and stays lit by itself. One lamp segment in some columns would light the machine corners."),
}

TAGCLASS = {"Current": "cur", "Before 0.71.0": "old", "Recommended": "rec", "Good add-on": "ok", "Optional extra": "ok"}

CSS = """
:root{--bg:#f4f3ef;--fg:#1d1d1f;--dim:#6b6b70;--card:#fff;--line:#e2e0da;--rec:#1f7a3a;--ok:#2d5f9a;--no:#9a2d2d;--cur:#6b5a1e;--old:#6b6b70}
@media (prefers-color-scheme: dark){:root:not([data-theme="light"]){--bg:#17171a;--fg:#ececef;--dim:#9a9aa2;--card:#212126;--line:#33333a;--rec:#5fc27c;--ok:#7fb0ea;--no:#e08080;--cur:#d6bf6a;--old:#a0a0a8}}
:root[data-theme="dark"]{--bg:#17171a;--fg:#ececef;--dim:#9a9aa2;--card:#212126;--line:#33333a;--rec:#5fc27c;--ok:#7fb0ea;--no:#e08080;--cur:#d6bf6a;--old:#a0a0a8}
body{background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,Segoe UI,sans-serif;margin:0;padding:24px 16px}
main{max-width:1060px;margin:0 auto}
h1{font-size:24px;margin:0 0 6px}h2{font-size:18px;margin:0}
.lead{color:var(--dim);margin:0 0 20px}
.rec-box{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--rec);border-radius:8px;padding:12px 16px;margin-bottom:20px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin-bottom:18px}
.top{display:flex;gap:10px;align-items:center;flex-wrap:wrap}
.tag{font-size:12px;font-weight:600;padding:2px 8px;border-radius:99px;border:1px solid currentColor}
.tag.rec{color:var(--rec)}.tag.ok{color:var(--ok)}.tag.no{color:var(--no)}.tag.cur{color:var(--cur)}.tag.old{color:var(--old)}
.place{color:var(--dim);margin:6px 0 10px}
.pics{display:grid;grid-template-columns:1fr 1fr;gap:10px}
figure{margin:0}figure img{width:100%;border-radius:6px;display:block}
figcaption{font-size:12px;color:var(--dim);margin-top:3px}
table{width:100%;border-collapse:collapse;font-size:13px;margin-top:8px;display:block;overflow-x:auto}
th,td{text-align:left;padding:5px 8px;border-top:1px solid var(--line);vertical-align:middle}
th{color:var(--dim);font-weight:500}
.thumb{width:72px;height:72px;border-radius:4px}
.sw{display:inline-block;width:12px;height:12px;border-radius:2px;margin-right:5px;vertical-align:-1px;border:1px solid var(--line)}
.dim{color:var(--dim)}code{font-size:12.5px}
@media (max-width:640px){.pics{grid-template-columns:1fr}}
"""

LEAD = ("Each set is placed the way the mine places it: against the rusted plate the mine puts behind its machines "
        "(3 blocks wide, floor to head height), with basalt beyond. The pictures are drawn from the game's own shape and "
        "texture files (Vintage Story 1.22.7), seen from above and to the side, and all are brightened by the same amount. "
        "They are not in-game screenshots, so mine lighting is not shown.")

REC = ("<b>Recommendation: A, with B as an extra.</b> A keeps today's column and its snug hitboxes, but gives it a start "
       "and an end in the wall and breaks up the plain rod with a cracked segment. B brings back a ceiling pipe like the one "
       "you had before, now rusty iron with a hitbox along its whole length. E (one lit Jonas pipe segment in some columns) "
       "is optional. C and D are here so you can see why they lose.")


def img64(name):
    with open(os.path.join(OUT, name), "rb") as fh:
        return "data:image/png;base64," + base64.b64encode(fh.read()).decode()


def main():
    sets = json.load(open(os.path.join(OUT, "sets.json"), encoding="utf-8"))
    by = {s["key"]: s for s in sets}
    cards = []
    for k in ["now-column", "old-ceiling", "A", "B", "E", "C", "D"]:
        s = by[k]
        tag, head, body = VERDICT[k]
        rows = []
        for t in s["singles"]:
            past = (" <span class='dim'>(sticks out " + html.escape(t["past"]) + ")</span>") if t["past"] else ""
            rows.append(
                "<tr><td><img class='thumb' src='%s' alt=''></td><td><code>%s</code><br><span class='dim'>%s</span></td>"
                "<td><span class='sw' style='background:%s'></span>%s</td><td>%s%s</td><td><code>%s</code></td><td>%d%%</td></tr>"
                % (img64(t["file"]), html.escape(t["type"]), html.escape(t["block"]), t["colour"], t["brightness"],
                   html.escape(t["size"]), past, html.escape(t["col"]), round(100 * float(t["cover"]))))
        cards.append(
            '<section class="card"><div class="top"><span class="tag %s">%s</span><h2>%s</h2></div>'
            '<p class="place">%s</p><div class="pics">'
            '<figure><img src="%s" alt=""><figcaption>How it looks</figcaption></figure>'
            '<figure><img src="%s" alt=""><figcaption>Hitboxes (red) and block cells (grey)</figcaption></figure></div>'
            '<p><b>%s.</b> %s</p><table><thead><tr><th></th><th>Block code and type</th><th>Brightness</th>'
            '<th>Model size (blocks)</th><th>Collision box</th><th>Model inside box</th></tr></thead><tbody>%s</tbody></table></section>'
            % (TAGCLASS.get(tag, "no"), tag, html.escape(s["title"]), html.escape(s["place"]),
               img64(k + "-look.png"), img64(k + "-hitbox.png"), head, html.escape(body), "".join(rows)))
    page = ('<!doctype html><html lang="en"><head><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width,initial-scale=1"><title>Tin Mine Pipes</title>'
            '<style>%s</style></head><body><main><h1>Tin mine pipes</h1><p class="lead">%s</p>'
            '<div class="rec-box">%s</div>%s</main></body></html>' % (CSS, LEAD, REC, "".join(cards)))
    path = os.path.join(OUT, "index.html")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(page)
    print("wrote", path, os.path.getsize(path) // 1024, "KB")


if __name__ == "__main__":
    main()
