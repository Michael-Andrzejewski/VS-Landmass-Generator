# Rustfall test worlds

Build a complete Rustfall world headless (no game client), inspect it, and
install it as Michael's `Rustfall.vcdbs`.

## Build

    python mkworld.py rfworld1 42430 Rustfall              # his live world plan
    python mkworld.py rfx1 42431 pilot-test plan_extras.txt  # a few islands only

    PILOT_BRIDGE_PORT=4601 VintagestoryServer.exe --dataPath "<folder printed above>"

- Run the server in the background. The Rustfall setup runs by itself on first
  load. Wait for `World setup finished` in `data/Logs/server-main.log` (about 10
  minutes for the full plan), not for the word "Exception": other mods in the
  pack log harmless exceptions at boot (overhaullib, rubberbandaid).
- `PILOT_BRIDGE_PORT` must differ from the game port. With both on one port,
  the bridge fails to start and curl talks to the game socket.
- Talk to it through Pilot: `curl "http://127.0.0.1:<bridge>/cmd?c=gencount%200%200%2060"`.
  Leave the leading `/` off the command: Git Bash rewrites `/gencount` into a
  Windows path before Python or curl sees it.
- Stop it with `cmd?c=stop` and wait for the process to exit. A force kill
  leaves "Duplicate entity id" warnings and loses unsaved work.

## Inspect

- `/gendumparea x z radius name` writes `LandmassGenerator/dumps/<name>.lmd`,
  a gzip (header json, then runs of (count u32, palette index u16) over y, z, x).
  `knollcheck.py` shows how to read one.
- `/gencount x z radius` logs the creatures and dropped items around a spot.
- `/genstorycheck <code>` logs how much of a story structure's schematic stands,
  with a map by chunk column. After a full `/genmapexport`, check `village`:
  Rustfall 0.70's three-hour export left two of its chunk columns bare land
  (the smithy and 24 of its 32 chests gone), though a fresh world builds it
  whole. `/genstorycheck village repair` generates any column under half again.
  Fresh-world readings: village 100%, Lazaret 92%, Tobias' Cave 75%, Resonance
  Archives 74%; the rest is terrain inside their schematics, so their edge
  columns read low by nature and want no repair.
- A headless server keeps almost nothing loaded. A `/build` script or a spawner
  far from spawn silently does nothing there. Test those with a real client
  (Pilot, see its README), standing at the spot.

## Install

1. Make sure no `Vintagestory.exe` is running.
2. Back up the current `%APPDATA%\VintagestoryData\Saves\Rustfall.vcdbs`. Its
   modified time tells you whether Michael has played it since the last
   install.
3. Copy the new save over it.

## Client tests with Pilot

`spawnertest.py` and `radiustest.py` walk a joined player up to spawners and
check what spawns. They read the debug log and `/gencount`. Copy the test save
to `Saves\pilot-<name>.vcdbs.vcdbs` and launch it with the mouse-guard
launcher, `--openWorld pilot-<name>.vcdbs`. The Pilot README explains why the
name matters.

## Mine giants

- `astarcheck.py <dump> <outdir> x,y,z ...` walks a giant's box (4.8 x 4) from
  each spawner by the game's own pathfinding rules (step up only with the body
  raised by the step height; no step down while part of the body is still on
  the higher floor) and says what blocks its spawn box. Dump the mine with
  `/gendumparea 1915 -1605 85` (or set `CX`, `CZ`, `CR` to match your dump).
  A plain box flood fill is not enough: it passed runs the real giant never
  climbed.
- `chase.py <spawner x,y,z> <stand x,y,z> <next x,y,z> ...` is the real test,
  in a Pilot client: it stands the player inside the wake range, follows the
  giant through `/gencount` (which logs every giant's position), and moves the
  player on along the given points whenever the giant comes within 14 blocks.
  The build's debug log lists each lair's run points for the route.
