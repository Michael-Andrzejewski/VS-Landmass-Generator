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
- For structures and anything block-level, use the EXACT pipeline instead of
  the approximate shape preview: `node tools/dumpgen.mjs <shape>` generates
  the island for real on a headless server and writes a block dump the
  previewer renders voxel for voxel (dropdown group "exact dumps"). This is
  the ground truth; verify there before asking Michael. The server STAYS
  RUNNING between runs (fast loop: one island build per iteration, no
  reboot); after a DLL change `dotnet build -c Release` and rerun, dumpgen
  restarts the server itself. `--stop` shuts it down. Dumps are bare
  (generator-placed columns only) by default; use `--full` when checking
  how a structure meets natural terrain, since bare dumps hide the natural
  seabed. The server uses its own data folder under export/ and its own
  port; it never touches the real game data or a running client.
- Shape files (`shapes/*.txt`, copied to `%APPDATA%\VintagestoryData\LandmassGenerator\`)
  are read fresh at command time: no rebuild, no restart. DLL changes need
  `dotnet build -c Release` (auto-deploys the zip) AND a world restart.
- Verify block codes against `%APPDATA%\Vintagestory\assets\survival\`, never
  from memory. Read the chat problems note after every /genisland run.
