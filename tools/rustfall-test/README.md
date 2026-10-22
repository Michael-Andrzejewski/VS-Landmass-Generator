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
