# Clutter prop catalog, Vintage Story 1.22.7

This folder lists every clutter prop in the game: its look, its size and its hitboxes in game. It is meant for world generation. Use it to pick props that fit Rustfall, and to avoid props that look or behave wrong.

## Files

- `clutter-catalog.csv` has one row per placeable type, 1,461 rows.
- `clutter-catalog-raw.json` holds the same rows with exact numbers (colours as RGB, bounds as floats, every texture flip option).
- `build_clutter_catalog.py` builds both. `vsjson.py` reads the game's loose JSON.

Rebuild after a game update from any folder outside the game:

```
python -I "C:\Users\maaro\OneDrive\Desktop\Games\Vintage Story\vs-landmass-generator\tools\clutter-catalog\build_clutter_catalog.py"
```

It reads `%APPDATA%\Vintagestory\assets` by default (`--assets` changes it, `--out` changes the output folder). It needs numpy and Pillow and takes about two minutes. It only reads game files.

| Block code | Types | Notes |
|---|---|---|
| `clutter` | 970 | the main prop block |
| `clutter-devastation` | 184 | machinery, pipes, gears, tanks |
| `clutteredbookshelf` | 138 | bookshelf halves; the BE also needs `Variant` |
| `clutteredbookshelf-agedacacia` | 69 | same shelves in aged acacia |
| `clutter-aquatic` | 38 | anchors, boats, whale bones, wreckage |
| `jonas-lamp` | 28 | Jonas lamps (block file `machine/jonas/jonaslamp.json`) |
| `clutteredbookshelfwithlore` | 21 | lore bookshelves |
| `banner-banner`, `banner-segment`, `banner-bottom` | 10, 1, 1 | Hansa banners |
| `clutter-elevator-elevatorrail` | 1 | elevator rail with a toggleable box |

The builder finds these blocks itself. It scans every blocktype for the classes `BlockClutter`, `ToggleCollisionBox`, `BlockClutterBookshelf` and `BlockClutterBookshelfWithLore`, so a new clutter file in a later version shows up on its own.

## How the game builds a prop

I read this from the decompiled 1.22.7 `VSSurvivalMod.dll` (`BlockShapeFromAttributes`, `BlockClutter`, `ClutterTypeProps`, `BEBehaviorShapeFromAttributes`, `Cuboidf`). It was not guessed from the JSON.

- **Shape.** The block entity's `Type` picks an entry in the block's `types`. The shape file is `shapes/<shapeBasePath>/<shapePath or code>.json`.
- **Textures.** The shape's own `textures` map comes first. The block's `attributes.textures` replaces entries, then the type's `textures` replaces entries. The block-level `textures` key is not used for these meshes.
- **Texture flip.** If the block entity has `overrideTextureCode` set, the texture named by the type's `textureFlipCode` is swapped for that entry of `overrideTextureGroups[textureFlipGroupCode]`. Only that one texture code changes. 401 types have a flip with a visible effect. In 63 types the flip names a texture code missing from the shape, so the flip does nothing. Most of these are machinery: all the `column*`, `tank1*`, `tank2*`, `swing*`, `junktanksmall*` and `pad*` types.
- **Collision.** The type's `colSelBoxes` are rotated with the block entity's rotation. Then they are clamped to the 0 to 1 block, then shifted by `offsetX/Y/Z`. A box can never reach into a neighbouring block, except by the offset. At a yaw that is not a right angle, the box becomes the axis-aligned bounds of the turned box, so it grows.
- **No `colSelBoxes`.** The block's own boxes apply. For every clutter block here that is a full cube. Banners are the exception (`collisionSelectionBox`, a thin pole).
- **Selection.** The type's `selBoxes` if present, else the collision boxes.
- **Key names.** Names are matched without regard to case. Any other name is ignored without a warning. Nine devastation types write `collisionbox: null` and `selectionbox`, and six palisade types write `canStep`. None of these do anything.
- **Inverted boxes.** A box written with x2 below x1 is normalised by the rotate step, so it still works.
- **Type rotation.** A type's `rotation` turns the model but not its boxes. Only the five `toolrack-*` types use it, and their boxes are already written for the turned model.
- **Game material.** No type sets `material`, so the game treats every clutter prop as Stone (`ClutterTypeProps` default).
- **Block entity fields worth setting** in worldgen: `Type`, `rotateY` (saved as `meshAngle`, placement snaps to 22.5 degrees), `rotateX`, `rotateZ`, `offsetX/Y/Z` (in blocks; the wrench moves 1/16), and `overrideTextureCode`. A bookshelf also needs `Variant`, and a double-sided one also needs `Type2`.

## How the colours were worked out

The builder walks every shape element with its full transform: parent chain, rotation origin, rotations in the game's X then Y then Z order, and scale. For each enabled face it resolves the texture in the order above. It finds the face's UV rectangle in the PNG, scaled by the shape's `textureWidth`/`textureSizes` as the tesselator does. It takes the mean colour of those pixels, weighted by alpha so transparent pixels count for nothing. Overlays are painted onto the base first. Each face then counts in proportion to its area in the model. So a prop with a small brass dial on a big rusty body reads as rusty.

- `color_hex` is that average. `brightness` is the Rec. 709 luma of the sRGB values (0 is black, 1 is white). `saturation` is HSV saturation. `hue` is a plain word from both.
- For reference, metal props have a median brightness of 0.31. The rusty iron textures sit at about 0.19 to 0.20.
- `material` comes from the texture paths, weighted the same way. It says "mixed" when no material covers 70 percent; `material_mix` gives the shares.
- `condition_name` comes from words in the type name. `condition_textures` comes from texture folders such as tarnished, corroded and charred.
- `flip_options` gives the colour after each possible texture flip. `best_rust_flip` names the darkest rusty choice.

Limits. Faces hidden inside other elements still count. The game's face shading and the light in a mine are not modelled, so top faces will look lighter in game and everything looks darker underground. Random texture alternates are ignored and blended overlays are treated as plain overlays. Seven types have faces that use the texture code `#null`. The game draws its unknown texture there; the CSV notes how much area that is.

## How sizes and hitboxes were worked out

- `size_blocks`, `bbox_min` and `bbox_max` are the model's bounds in blocks at rotation 0, from the corners of every element with a visible face. +x is east, +z is south, and the block is 0 to 1. `past_block` lists how far the model reaches into each neighbour.
- `placement` is a guess from those bounds: floor scrap, floor-standing, ceiling-hung, wall-mounted, floor-to-ceiling, or floating in the block.
- The model is also turned into 1/16 voxels. `box_covers_model` is the share of the model inside its own block that the collision boxes cover. `box_filled_by_model` is the share of the box that has model in it.
- `hitbox_flags` marks: no `colSelBoxes` (full cube); a full-block box on a small prop; a box much larger than the model; a box covering under half the model; a box offset from the model; no collision on a solid-looking prop; a box clamped by the game; and a model sticking out of its block.

Limits. Everything is measured at rotation 0. Thin parts count as whole voxels, so thin props look a little fuller than they are. The flags use fixed thresholds, so read them as hints. No claim here was tested in a running game.

## The two pale pipes

`pipelong3-aged` and `pipelong4-aged` look pale because 75 to 81 percent of their face area uses `block/metal/tarnished/tinbronze-sheet1`. That texture is a light yellow-olive mottle with an average brightness of 0.49, so the pipes come out khaki at 0.46 and 0.42. "Aged" here means tarnished tin-bronze, not rust, and it is more than twice as light as the rusty iron textures (about 0.19). They are also ceiling pipes. The body hangs in the top 0.4 of the block on aged wooden planks with nails, and the pipe is 3 or 4 blocks long, running south from its own block. Their only box is x 0.3 to 0.7, y 0.6 to 1, z 0 to 1: a strip along the top of the first block. The rest of the pipe has no hitbox and pokes into the next 2 or 3 blocks.

The fix for the colour is the texture flip. Their flip code `tinbronze1` covers the pipe body. Set the block entity's `overrideTextureCode` to `rustysheet` (about #4a3222, brightness 0.21) or `veryrustyiron` (about #4d3624, 0.23). For a wall, the `pipe-veryrusted-*` types in plain `clutter` are better: they are one block each and their boxes match.

## Rusty and dark metal for mines and machinery

### Wall fittings and pipe runs (the ten best for a mine wall)

| Block | Type | Colour | Why |
|---|---|---|---|
| `clutter` | `pipe-veryrusted-v-empty` | #422e20, 0.19 | vertical rusty pipe, 1 block, box matches |
| `clutter` | `pipe-veryrusted-h-EW` | #432e1f, 0.19 | horizontal run, box matches |
| `clutter` | `pipe-veryrusted-v-NSW` | #412d20, 0.19 | T junction, box matches |
| `clutter` | `pipe-veryrusted-h-SW` | #432e1f, 0.19 | elbow, box matches |
| `clutter` | `chains/manacles-wall` | #412e21, 0.19 | flat against the north side, thin box |
| `clutter` | `wallhook-mechsection` | #443124, 0.20 | wall bracket holding a machine part |
| `clutter` | `wallhook-arm2` | #483226, 0.21 | wall bracket, rusty arm |
| `clutter` | `fence/iron/ew1` | #56443c, 0.28 | rusty iron grate panel, centred in z |
| `clutter-devastation` | `junkpipe3` | #402c1f, 0.19 | rusty pipe bend; slab box y 0.25 to 0.75 |
| `jonas-lamp` | `lightsource/on/lumi-pipe-v-lamp` | #473e2a, 0.24 | rusty pipe lamp, gives light, box matches |

The pipe types are centred in their block. Use `offsetX`/`offsetZ` to push one against the rock; the boxes move with it. The other `pipe-veryrusted-*` junctions, `wallhook-arm1/arm3/leg1/leg2`, `lantern/wall4` and `elevatorrail-aged` are good too. `lantern/wall4` and `elevatorrail-aged` cover under half their model with their box.

### Big wall pieces and set pieces

- `gearhugemetal9` (9.6 blocks across) and `gearhugemetal15` (16 blocks) in `clutter-devastation`: dark rusty gears at 0.17 to 0.18, with a one-block box at the hub.
- `brokengear/big01..03` and `brokengear/small01..03`: rusty broken gears from 9 to 17 blocks across. "small" is still about 9 by 11 by 7 blocks. Each has a one-block box.
- `boilerdoor-aged`: a dark riveted hatch, 1 by 1, at 0.19. Its box covers z 0 to 0.5 while the door sits at z 0.44 to 0.81.
- `vtankmid`, `vtankend`, `vtankend2`: dark grey lead tanks, 3 to 4 blocks tall and almost 3 deep, at 0.21.
- `junkreflectorwhole1`, `junkreflectorhalf1`: brown rusty dishes about 3 tall, at 0.28 to 0.29.
- `rivetedmetal`: a full riveted cube, brown at 0.36. With `overrideTextureCode` `rustysheet` it drops to 0.19.

### Standing machinery

- `junktanksmall1`, `junktanksmall2`, `junktanksmallbase`, `junktanksmalltop2`: rust-orange tanks about one block, at 0.22, full-block boxes.
- `barrel-metal1..4` (`clutter`): dark rusty drums at 0.18, full cube.
- `machine4`, `machine6`, `machine7`, `mech-part5`: rusty machine parts under one block, with tight boxes.
- `mech/part10`, `mech/part11`, `mech/part12`: dark parts about 2 tall; the box covers only the first block.
- `brazier3`, `brazier4`, `golem-head`: dark rusty, tight boxes.
- `tank2-aged` (3 tall, 0.33) and `column6-aged` (4 tall, 0.31): big rusty uprights with one-block boxes.
- `hazmat-rack-empty`: dark grey rack, 2 tall, full-cube box.

### Floor scraps

| Block | Type | Colour | Note |
|---|---|---|---|
| `clutter` | `scrap-art10` | #44302c, 0.20 | corroded scrap sheet, 0.14 high, box matches |
| `clutter` | `chains/chain-floor` | #432e21, 0.19 | loose rusty chain, 0.16 high |
| `clutter` | `machine2` | #432e20, 0.19 | flat rusty machine plate, 0.27 high |
| `clutter` | `pile-precisiontools` | #503928, 0.24 | rusty tool pile, 0.08 high |
| `clutter` | `pile-weapon3` | #4b423a, 0.26 | dark tarnished iron pile, 0.08 high |

Good runners-up: `scrap-art9`, `scrap-art6`, `chains/manacles-floor`, `pile-drafting-instrument`, `bell-parts`, `bell-spine` (near black, 0.07), `golem-hand1`, `golem-hand2`, `anvil-vise`, `tablet-metal7..9`.

## Pale or clean despite the name

All of these read light next to rusty iron. The `best_rust_flip` column says which ones a texture flip can fix.

- Tarnished tin-bronze, khaki, 0.42 to 0.50: `pipelong2-aged`, `pipelong3-aged`, `pipelong4-aged`, `pipelong4bent-aged`, `sightglass1..4-aged`, `switch1..3-aged`, `valve1-aged`, `valve2-aged`.
- Ingot tin-bronze, gold-orange: `porthole1-aged` and `porthole2-aged` (0.43 to 0.46). The plain `gauge*`, `valve1/2`, `switch1..3`, `sightglass1/2` and `porthole*` are brighter still (0.53 to 0.61).
- Plain tin-bronze sheet: `pipelong2`, `pipelong3`, `pipelong4` and `pipelong4bent` without `-aged` are 0.53 to 0.54.
- Tarnished bismuth bronze, khaki, about 0.45 (`clutter`): `anvil-broken2`, `censer/metal2-ruined1..3`, `coatrack-ruined1..3`.
- Tarnished copper, light brown, about 0.42: `bed/bed-metal-ruined1..3`.
- Light grey lead: `misc1-aged`, `miscmedium2-aged` (0.42).
- Clean grey iron sheet, 0.31 to 0.37: `boilerbody`, `boilerend`, `boilerdoor`, `tower1..3`, `towerbottom`, `gear12`, `gear24`, `gear36`, `gearbox1`, `tanksmallmid1`, `tanksmalltop2-aged`. They read as plain grey steel. `rustysheet` turns them to about 0.19.
- Glowing green parts: `tank1`, `tank2`, `sightglass3`, `sightglass4`, `porthole2`, `porthole3` and `spark5` use green glowing translocator textures (`block/machine/statictranslocator/...`). Of their `-aged` versions, only `tank1-aged`, `tank2-aged` and `porthole3-aged` turn rusty; `spark5-aged` stays green.

## Odd hitboxes

- **Invisible walls.** `junkchain1..6` and `junkelevatorrail1..3` set `collisionbox: null`, and the game ignores that key. They fall back to a full-cube box for collision and selection, so a thin chain blocks the whole block.
- **Box nowhere near the model.** `pipe1` has its boxes along the top of the block, while the model is a stub in the middle running south. They overlap 0 percent.
- **Box offset from the model.** `boilerdoor-aged` (above), `gear1290`, `lantern/wall4`, `geartrain3`, `vtankmid` and `vtankend`, among 122 flagged types.
- **One block of box on a huge model.** The giant gears, `brokengear/*`, `swing1`/`swing2` (12 blocks tall), `column*`, `boilerbody` (1 by 3 by 2) and `boat-trader` (12 by 9 by 6, with a flat box 0.06 high). 587 types reach past their block by more than 1/16.
- **Full cube on a small prop.** Besides the chains: `wreckage1..4`, `oar1..3`, `driftwood1/2/4` and `bone/spine1/2` in `clutter-aquatic`.
- **No `colSelBoxes`, so a full cube.** 96 types, most of them sensible (large crates, metal barrels, coffins, beds, metal tables and shelves, `hazmat-rack*`, every `*-evaporating` type, `pipe-veryrusted-v-NESW`).
- **No collision at all.** `padtop` and `padtop-aged` (empty `colSelBoxes`), `tablet-clay1..6`, `tablet-metal1..6`, `tablet-stone1..6`, `claylamp-ruined1..3`, `pile-woodworkingtools-clean` and `pile-tobias-gadgets2`. That last one is a pile half a block high, and players walk through it. All of them can still be selected.
- **Clamped boxes.** 45 types write boxes past the block edge (for example `barricade6` up to y 1.12), and the game cuts them back to the block.

## Wood, stone, bone and organic worth knowing

- **Dark wood for mines.** `ladder-aged1..5` (0.16, climbable; the `-noclimb` copies look the same), `rubble-wood1..4` (charred floor debris, 0.23 to 0.24), `barricade2` (0.18), `crate/crate-small-*` and `crate/crate-medium-*` (0.20, tight boxes), `chestrubble`, the `junkbeam*` beams in `clutter-devastation` (0.21), and `junkreflector3/4` (wood-framed, 0.21). Large crates fall back to full cubes, which suits them.
- **Stone.** `rubble-cracked1..4` (grey-brown rubble heaps, 0.42 to 0.43, which reach a little into neighbours), `grinding-stone` (near black, 0.13), `gravestone-2/4`, `tablet-stone1..9` (1 to 6 have no collision). `mortar-stone` is very pale (0.77).
- **Bone reads pale.** Every bone prop is 0.64 to 0.81: `pile-bones1/2`, `skeleton-*`, `carcass-*`, and the aquatic whale `bone/*` set. They stand out strongly in a dark mine. That may suit a grisly spot but not background clutter.
- **Organic.** `candlestub*` (ochre wax, 0.49 to 0.52) and the straw beds (`bed/bed-straw-*`, 0.36 to 0.38). There is little else.

## What I checked by eye

I opened the actual texture PNGs, scaled up, next to the computed swatches. These matched by eye:

- the two pipes: `tinbronze-sheet1` is light yellow-olive, `rusty-iron2` is dark brown with orange rust, and the planks are pale aged wood. I also looked at the flip targets `iron-sheet1` (dark rust) and `sheet/lead1` (dark slate green).
- `rusty-iron` (the `pipe-veryrusted-*` pipes, chains, `machine2`), `aged-iron-fence-base` (`fence/iron/ew1`; its transparent background was correctly ignored), `corroded/normal3` (`scrap-art10`), `tarnished/iron` (`pile-weapon3`), `machinic` (`gearhugemetal9`) and `sheet/iron1` (`boilerbody`, clean grey).
- `tarnished/tinbronze` (pale khaki), `ingot/tinbronze` (bright gold), `sheet/cupronickel1` (pinkish tan) and `tarnished/bismuthbronze` (khaki).
- `stone/rubble` (`grinding-stone`), `wood/charred` (`rubble-wood1`), `debarked/veryrotten` (`ladder-aged1`), `stone/rubble/cracked1` (`rubble-cracked1`), and `creature/bone/bone5` with its overlay (`bone/ribcage1`).
- `wood/bookshelves/shelf1`, which shows rows of books. That is why bookshelf shelf faces count as paper.

Everything else in the CSV is computed only. No prop was placed and looked at in a running game.
