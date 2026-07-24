// Headless block-dump generator: boots a throwaway Vintage Story dedicated
// server (its own data folder under export/, its own port, never touching the
// real VintagestoryData), runs /genisland ... dump=1 for each requested shape,
// and leaves .lmd block dumps that the localhost previewer renders block for
// block. The same DLL builds the dump and the real world, so what the viewer
// shows IS what the game builds.
//
//   node tools/dumpgen.mjs lighthouse chainfield ...
//   node tools/dumpgen.mjs all              (every shape with a Suggested line)
//   node tools/dumpgen.mjs --keep-world ... (reuse the world; faster, but a
//                                            rebuild over older runs can leave
//                                            remnants and drained ocean)
//
// The export world is recreated fresh each run by default, so every dump is
// built on virgin ocean. Each shape's /genisland options come from its
// "# Suggested:" header line.
import { spawn, spawnSync } from 'child_process';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const appdata = process.env.APPDATA;
const serverExe = path.join(appdata, 'Vintagestory', 'VintagestoryServer.exe');
const realMods = path.join(appdata, 'VintagestoryData', 'Mods');
const dataPath = path.join(repo, 'export', 'data');
const exportMods = path.join(dataPath, 'Mods');
const shapeDir = path.join(dataPath, 'LandmassGenerator');
const dumpDir = path.join(shapeDir, 'dumps');
const PORT = 42425;

// Mods the generator's output depends on. Everything else in the player's
// mod folder is irrelevant to island geometry and only slows the boot.
const MOD_PREFIXES = ['LandmassGenerator_', 'UnderwaterHorrors_'];

function log(msg) { console.log(`[dumpgen] ${msg}`); }
function fail(msg) { console.error(`[dumpgen] ERROR: ${msg}`); process.exit(1); }

function newestZip(prefix) {
  const hits = fs.readdirSync(realMods).filter(f => f.startsWith(prefix) && f.endsWith('.zip'));
  if (!hits.length) return null;
  hits.sort((a, b) => fs.statSync(path.join(realMods, b)).mtimeMs - fs.statSync(path.join(realMods, a)).mtimeMs);
  return hits[0];
}

function suggestedOptions(shapeName) {
  const file = path.join(repo, 'shapes', shapeName + '.txt');
  if (!fs.existsSync(file)) fail(`no shape file shapes/${shapeName}.txt`);
  const m = fs.readFileSync(file, 'utf8').match(/Suggested:\s*\/genisland\s+([^\r\n]+)/);
  if (!m) fail(`shapes/${shapeName}.txt has no "# Suggested: /genisland ..." line`);
  return m[1].trim();
}

// Stable per-shape seed so re-running a dump after a code change shows the
// code change, not a different random coastline.
function seedFor(name) {
  let h = 0;
  for (const c of name) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  return 1000 + (h % 100000);
}

function prepare(reset) {
  if (!fs.existsSync(serverExe)) fail(`server not found at ${serverExe}`);
  fs.mkdirSync(exportMods, { recursive: true });
  fs.mkdirSync(shapeDir, { recursive: true });
  fs.mkdirSync(dumpDir, { recursive: true });

  if (reset) {
    const saves = path.join(dataPath, 'Saves');
    if (fs.existsSync(saves)) { fs.rmSync(saves, { recursive: true, force: true }); log('export world recreated (default; pass --keep-world to reuse)'); }
  }

  // Fresh copies of the needed mod zips only.
  for (const f of fs.readdirSync(exportMods)) fs.rmSync(path.join(exportMods, f));
  for (const prefix of MOD_PREFIXES) {
    const zip = newestZip(prefix);
    if (!zip) { log(`WARNING: no ${prefix}*.zip in ${realMods}`); continue; }
    fs.copyFileSync(path.join(realMods, zip), path.join(exportMods, zip));
    log(`mod: ${zip}`);
  }

  // Fresh copies of every shape file.
  for (const f of fs.readdirSync(path.join(repo, 'shapes')))
    if (f.endsWith('.txt')) fs.copyFileSync(path.join(repo, 'shapes', f), path.join(shapeDir, f));

  // Server config: generate once, then enforce our settings every run.
  const cfgPath = path.join(dataPath, 'serverconfig.json');
  if (!fs.existsSync(cfgPath)) {
    log('generating serverconfig.json');
    const r = spawnSync(serverExe, ['--dataPath', dataPath, '--genconfig'], { timeout: 120000 });
    if (!fs.existsSync(cfgPath)) fail('server --genconfig produced no serverconfig.json: ' + r.stdout);
  }
  const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
  cfg.Port = PORT;
  cfg.Upnp = false;
  cfg.AdvertiseServer = false;
  cfg.MaxClients = 1;
  cfg.PassTimeWhenEmpty = true;
  // Relative 'Mods' is the game install's own folder holding the base game
  // systems (game/survival/creative); removing it breaks everything.
  cfg.ModPaths = ['Mods', exportMods];
  cfg.WorldConfig = cfg.WorldConfig || {};
  cfg.WorldConfig.SaveFileLocation = path.join(dataPath, 'Saves', 'dumpworld.vcdbs');
  cfg.WorldConfig.WorldName = 'LandmassGenerator dump world';
  cfg.WorldConfig.Seed = '424242';
  cfg.WorldConfig.PlayStyle = 'surviveandbuild';
  cfg.WorldConfig.WorldType = 'standard';
  // Pure ocean, exactly like a Rustfall world: the natural seabed around an
  // island in the dump then matches the real thing.
  cfg.WorldConfig.WorldConfiguration = Object.assign(cfg.WorldConfig.WorldConfiguration || {}, {
    landcover: '0',
    upheavelCommonness: '0',
    lgPureOcean: true,
  });
  fs.writeFileSync(cfgPath, JSON.stringify(cfg, null, 2));
}

function run(shapes) {
  // The server console ignores piped stdin, so the mod reads its commands
  // from dumpjobs.txt at boot, runs them in sequence, and stops the server
  // itself after the last dump.
  const jobLines = shapes.map((name, i) => {
    let opts = suggestedOptions(name);
    if (!/(^|\s)seed=/.test(opts)) opts += ` seed=${seedFor(name)}`;
    // Islands go side by side, far enough apart that no two ever overlap.
    return `${opts} x=${i * 2000} z=0 dump=1`;
  });
  fs.writeFileSync(path.join(shapeDir, 'dumpjobs.txt'), jobLines.join('\n') + '\n');
  const expected = shapes.map(n => path.join(dumpDir, n + '.lmd'));
  for (const f of expected) if (fs.existsSync(f)) fs.rmSync(f);

  log(`starting headless server (port ${PORT}), ${shapes.length} dump job(s)`);
  const srv = spawn(serverExe, ['--dataPath', dataPath], { stdio: ['ignore', 'pipe', 'pipe'] });
  let buf = '';

  const onLine = (line) => {
    if (process.env.DUMPGEN_VERBOSE) console.log('  | ' + line);
    if (/\[dump\]|Island complete|Island FAILED|Building '|Critical error/.test(line)) log('server: ' + line.replace(/^.*?\[Server [A-Za-z]+\]\s*/, ''));
  };
  srv.stdout.on('data', d => {
    buf += d.toString();
    let nl;
    while ((nl = buf.indexOf('\n')) >= 0) { onLine(buf.slice(0, nl).trim()); buf = buf.slice(nl + 1); }
  });
  srv.stderr.on('data', d => console.error('  ! ' + d.toString().trim()));

  const deadline = setTimeout(() => {
    console.error('[dumpgen] global timeout, killing server');
    try { srv.kill(); } catch { /* already gone */ }
  }, (10 + shapes.length * 15) * 60 * 1000);

  srv.on('exit', code => {
    clearTimeout(deadline);
    const missing = shapes.filter((n, i) => !fs.existsSync(expected[i]));
    if (missing.length) { console.error(`[dumpgen] server exited (${code}); missing dumps: ${missing.join(', ')}`); process.exit(1); }
    log('refreshing viewer/blockcolors.json');
    const py = spawnSync('python', [path.join(repo, 'tools', 'gen_blockcolors.py')], { stdio: 'inherit', timeout: 300000 });
    if (py.status !== 0) console.error('[dumpgen] gen_blockcolors.py failed; dump colors may be stale');
    log(`done. Dumps in ${dumpDir}`);
    process.exit(0);
  });
}

const args = process.argv.slice(2);
const reset = !args.includes('--keep-world');
let shapes = args.filter(a => !a.startsWith('--'));
if (shapes.length === 1 && shapes[0] === 'all') {
  shapes = fs.readdirSync(path.join(repo, 'shapes'))
    .filter(f => f.endsWith('.txt') && /Suggested:\s*\/genisland/.test(fs.readFileSync(path.join(repo, 'shapes', f), 'utf8')))
    .map(f => f.replace(/\.txt$/, ''));
}
if (!shapes.length) fail('usage: node tools/dumpgen.mjs [--reset] <shape> [shape ...] | all');

prepare(reset);
run(shapes);
