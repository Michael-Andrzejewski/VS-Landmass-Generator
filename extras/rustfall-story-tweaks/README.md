# Rustfall Story Tweaks

Tiny server-side content mod that shrinks the landmasses the game forces
around story locations (`/wgen story setpos` or natural placement). Made for
the Rustfall ocean world.

## What it changes

`landformRadius` per story structure in `worldgen/storystructures.json`:

| Structure | Vanilla | Patched | Approx. island width |
|---|---|---|---|
| Resonance Archives | 200 | 150 | ~650 to ~515 |
| Lazaret | 200 | 100 | ~650 to ~370 |
| Village | 200 | 160 | ~650 to ~540 |
| Devastation | 800 | unchanged | |
| Tobias' Cave | 200 | 150 | ~650 to ~515 |
| Treasure Hunter | 200 | 80 | ~650 to ~315 |

Values sit above each schematic's footprint with margin. If an island edge
cuts too close to a structure, raise that value and regenerate.

## Applying changes

1. Edit the patch values, then rebuild the zip (from this folder):
   `powershell Compress-Archive -Path * -DestinationPath "$env:APPDATA\VintagestoryData\Mods\RustfallStoryTweaks_1.0.0.zip" -Force`
2. Restart the world. The smaller radius takes effect on load.
3. Already-generated story islands keep their old size until their chunks
   are regenerated: stand at the site and run `/wgen delr 12` (21 for the
   devastation). This wipes ALL blocks in that square, including any player
   builds, and the area regenerates as you move around.
