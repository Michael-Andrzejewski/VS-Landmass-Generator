# VS Landmass Generator

A Vintage Story code mod that generates whole procedural landmasses from a single chat command. It began as an experiment inside [Building Commands](https://github.com/Michael-Andrzejewski/VS-Building-Commands) and was split out so it can stabilize on its own.

## `/genisland [key=value ...]`

Generates a procedural ocean island centred on where you stand. Because a landmass is far more blocks than a normal fill may touch at once, it does not run in one burst. It force-loads the island's chunks and then builds column by column across game ticks, committing one batch per tick, so even a 500-block island never freezes the server. An oak is grown at the summit once the ground is down.

The shape is a radial dome with a simplex-noise coastline. The height falloff is biased by compass direction, so one side eases down into a sandy beach while the opposite side drops as a stone cliff, and the seafloor deepens outside the coast.

Requires the `controlserver` privilege (you have it in single player). Only one island generates at a time.

### Options

Options are `key=value` tokens in any order. A lone leading number is read as `diameter`.

| Key | Default | Meaning |
| --- | --- | --- |
| `diameter` | 120 | Island width in blocks. |
| `height` | 40 | Peak height above sea level at the centre. |
| `water` | 30 | How far the surrounding seafloor drops below sea level. |
| `basedepth` | 20 | Solid rock depth below sea level under the island. |
| `beachdir` | `s` | Compass side (`n e s w`) that eases into a sand beach. |
| `cliffdir` | `n` | Compass side that drops as a stone cliff. |
| `seed` | random | Fixes the shape so a run is repeatable. |
| `stone` | `rock-granite` | Core block. |
| `soil` | `soil-medium-none` | Sub-surface dirt. |
| `grass` | `soil-medium-normal` | Grass-topped surface. |
| `sand` | `sand-granite` | Beach block. |

Example: `/genisland diameter=200 height=45 beachdir=s cliffdir=n seed=1234`.

Run it from open ocean for the cleanest result. On existing land it clears the terrain above the new island.

## Notes and current limits

This is an early, standalone build kept separate from Building Commands until it proves stable.

- Chunks are loaded with `KeepLoaded=false`, so for very large islands stay near the centre while it generates.
- No undo yet. Generate on a test world or somewhere you do not mind reshaping.

## Building

`dotnet build -c Release` compiles the mod and deploys `LandmassGenerator_<version>.zip` to `%APPDATA%\VintagestoryData\Mods`, replacing the previous build. Requires the Vintage Story API DLLs at `%APPDATA%\Vintagestory`.
