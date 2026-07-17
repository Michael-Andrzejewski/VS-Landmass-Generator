# VS Landmass Generator

Code mod: `/genisland` builds procedural islands (see README.md). Michael is
building 20-30 hand-designed islands for a custom world with it, iterating
via his in-game screenshots. You cannot run the VS client yourself.

Rules for working here:

- Before designing or editing an island: read [tips.md](tips.md) (workflow,
  design language, density rules of thumb, verified block codes).
- When anything misbehaves or a feature silently does nothing: read
  [papercuts.md](papercuts.md) BEFORE debugging. The cause is usually already
  in there, and it is structural (code/gate/cache/lifecycle), not tuning.
- When you learn something the hard way, append it to the right file in the
  same pass and push.
- Preview island designs yourself before asking Michael: `node viewer/serve.js`
  serves http://localhost:5184, rendering any shape file with exact cave
  paths. Iterate there first; his screenshots are for what the previewer
  cannot show (block textures, tint, feel).
- Shape files (`shapes/*.txt`, copied to `%APPDATA%\VintagestoryData\LandmassGenerator\`)
  are read fresh at command time: no rebuild, no restart. DLL changes need
  `dotnet build -c Release` (auto-deploys the zip) AND a world restart.
- Verify block codes against `%APPDATA%\Vintagestory\assets\survival\`, never
  from memory. Read the chat problems note after every /genisland run.
