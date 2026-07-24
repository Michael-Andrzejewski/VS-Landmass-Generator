// Headless block-dump generator: boots a throwaway Vintage Story dedicated
// server (its own data folder under export/, its own port, never touching the
// real VintagestoryData), runs /genisland ... dump=1 for each requested shape,
// and leaves .lmd block dumps that the localhost previewer renders block for
// block. The same DLL builds the dump and the real world, so what the viewer
// shows IS what the game builds.
//
//   node tools/dumpgen.mjs lighthouse chainfield ...
//   node tools/dumpgen.mjs all          (every shape with a Suggested line)
//   node tools/dumpgen.mjs --full ...   (keep natural terrain in the dump;
//                                        default dumps are BARE: only columns
//                                        the generator touched, so they load
//                                        and mesh much faster)
//   node tools/dumpgen.mjs --reset ...  (recreate the export world first)
//   node tools/dumpgen.mjs --stop       (shut the background server down)
//
// FAST LOOP: the server is left RUNNING after a run (dumpwatch.flag). The
// next dumpgen call hands jobs straight to it, so an iteration costs one
// island build instead of a ~35s boot. Every run builds at fresh, previously
// untouched coordinates (a persistent job counter), so no run can collide
// with an older one. If the mod zip is newer than the running server, the
// server is restarted automatically so dumps always use the current DLL.
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
const pidFile = path.join(repo, 'export', 'server.json');
const counterFile = path.join(repo, 'export', 'joboffset.txt');
const watchFlag = path.join(shapeDir, 'dumpwatch.flag');
const logFile = path.join(repo, 'export', 'server.log');
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

function liveServer() {
  try {
    const j = JSON.parse(fs.readFileSync(pidFile, 'utf8'));
    process.kill(j.pid, 0);           // existence check only
    return j;
  } catch { return null; }
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function stopServer(j) {
  log(`stopping server (pid ${j.pid})`);
  try { fs.rmSync(watchFlag); } catch { /* absent is fine */ }
  fs.writeFileSync(path.join(shapeDir, 'dumpjobs.txt'), '/stop\n');
  for (let i = 0; i < 30; i++) {
    await sleep(1000);
    try { process.kill(j.pid, 0); } catch { break; }
  }
  try {
    process.kill(j.pid, 0);
    log('server ignored /stop, killing it');
    spawnSync('taskkill', ['/pid', String(j.pid), '/t', '/f']);
  } catch { /* already gone */ }
  try { fs.rmSync(pidFile); } catch { /* fine */ }
  try { fs.rmSync(path.join(shapeDir, 'dumpjobs.txt')); } catch { /* consumed */ }
}

function prepare(reset) {
  if (!fs.existsSync(serverExe)) fail(`server not found at ${serverExe}`);
  fs.mkdirSync(exportMods, { recursive: true });
  fs.mkdirSync(shapeDir, { recursive: true });
  fs.mkdirSync(dumpDir, { recursive: true });

  if (reset) {
    const saves = path.join(dataPath, 'Saves');
    if (fs.existsSync(saves)) { fs.rmSync(saves, { recursive: true, force: true }); log('export world recreated'); }
    try { fs.rmSync(counterFile); } catch { /* fine */ }
  }

  // Fresh copies of the needed mod zips only.
  for (const f of fs.readdirSync(exportMods)) fs.rmSync(path.join(exportMods, f));
  for (const prefix of MOD_PREFIXES) {
    const zip = newestZip(prefix);
    if (!zip) { log(`WARNING: no ${prefix}*.zip in ${realMods}`); continue; }
    fs.copyFileSync(path.join(realMods, zip), path.join(exportMods, zip));
    log(`mod: ${zip}`);
  }

  copyShapes();

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

// Shape files are read fresh at command time, so a running server picks up
// edits: just copy them over before every run.
function copyShapes() {
  for (const f of fs.readdirSync(path.join(repo, 'shapes')))
    if (f.endsWith('.txt')) fs.copyFileSync(path.join(repo, 'shapes', f), path.join(shapeDir, f));
}

function writeJobs(shapes, full) {
  let n0 = 0;
  try { n0 = parseInt(fs.readFileSync(counterFile, 'utf8'), 10) || 0; } catch { /* first run */ }
  const jobLines = shapes.map((name, i) => {
    let opts = suggestedOptions(name);
    if (!/(^|\s)seed=/.test(opts)) opts += ` seed=${seedFor(name)}`;
    // Every job gets fresh, never-before-used coordinates, so a re-run can
    // never overbuild an older run's terrain.
    return `${opts} x=${(n0 + i) * 2000} z=0 dump=1${full ? ' dumpfull=1' : ''}`;
  });
  fs.writeFileSync(counterFile, String(n0 + shapes.length));
  fs.writeFileSync(watchFlag, 'keep the dump server alive after the jobs finish\n');
  fs.writeFileSync(path.join(shapeDir, 'dumpjobs.txt'), jobLines.join('\n') + '\n');
  return n0;
}

async function waitForDumps(shapes, pid) {
  const expected = shapes.map(n => path.join(dumpDir, n + '.lmd'));
  const t0 = Date.now();
  const deadline = t0 + (5 + shapes.length * 15) * 60 * 1000;
  const done = new Set();
  while (done.size < expected.length) {
    if (Date.now() > deadline) fail(`timed out; see ${logFile}`);
    if (pid) {
      try { process.kill(pid, 0); } catch {
        fail(`server died mid-run; tail of ${logFile}:\n` +
          fs.readFileSync(logFile, 'utf8').split('\n').slice(-25).join('\n'));
      }
    }
    for (let i = 0; i < expected.length; i++) {
      if (!done.has(i) && fs.existsSync(expected[i])) {
        done.add(i);
        log(`dump ready: ${shapes[i]}.lmd (${Math.round((Date.now() - t0) / 1000)}s)`);
      }
    }
    await sleep(1500);
  }
}

function finish() {
  log('refreshing viewer/blockcolors.json');
  const py = spawnSync('python', [path.join(repo, 'tools', 'gen_blockcolors.py')], { stdio: 'inherit', timeout: 300000 });
  if (py.status !== 0) console.error('[dumpgen] gen_blockcolors.py failed; dump colors may be stale');
  log(`done. Dumps in ${dumpDir}`);
  log('server left running for fast re-runs (node tools/dumpgen.mjs --stop to shut it down)');
}

const args = process.argv.slice(2);
const full = args.includes('--full');
const reset = args.includes('--reset');
let shapes = args.filter(a => !a.startsWith('--'));
if (shapes.length === 1 && shapes[0] === 'all') {
  shapes = fs.readdirSync(path.join(repo, 'shapes'))
    .filter(f => f.endsWith('.txt') && /Suggested:\s*\/genisland/.test(fs.readFileSync(path.join(repo, 'shapes', f), 'utf8')))
    .map(f => f.replace(/\.txt$/, ''));
}

if (args.includes('--stop')) {
  const j = liveServer();
  if (j) await stopServer(j); else log('no server running');
  process.exit(0);
}
if (!shapes.length) fail('usage: node tools/dumpgen.mjs [--full] [--reset] <shape> [shape ...] | all | --stop');

// Reuse the running server when it exists and its DLL is current.
let live = liveServer();
const zipName = newestZip('LandmassGenerator_');
const zipMtime = zipName ? fs.statSync(path.join(realMods, zipName)).mtimeMs : 0;
if (live && (reset || live.zipMtime !== zipMtime)) {
  log(reset ? 'reset requested, restarting server' : 'mod zip is newer than the running server, restarting');
  await stopServer(live);
  live = null;
}

// A very long-lived world would eventually run x offsets off the map edge.
// And a world with no job counter was built by the old always-reset flow:
// its coordinates are unknown, so it cannot be trusted not to collide.
let counter = 0;
try { counter = parseInt(fs.readFileSync(counterFile, 'utf8'), 10) || 0; } catch { /* fine */ }
const staleWorld = !fs.existsSync(counterFile) && fs.existsSync(path.join(dataPath, 'Saves'));
const mustReset = reset || staleWorld || counter + shapes.length > 200;

if (live) {
  copyShapes();
  const expected = shapes.map(n => path.join(dumpDir, n + '.lmd'));
  for (const f of expected) if (fs.existsSync(f)) fs.rmSync(f);
  writeJobs(shapes, full);
  log(`handed ${shapes.length} job(s) to the running server (pid ${live.pid})`);
  await waitForDumps(shapes, live.pid);
  finish();
} else {
  prepare(mustReset);
  const expected = shapes.map(n => path.join(dumpDir, n + '.lmd'));
  for (const f of expected) if (fs.existsSync(f)) fs.rmSync(f);
  writeJobs(shapes, full);
  log(`starting headless server (port ${PORT}), ${shapes.length} dump job(s), log: ${logFile}`);
  const out = fs.openSync(logFile, 'w');
  const srv = spawn(serverExe, ['--dataPath', dataPath], { detached: true, stdio: ['ignore', out, out] });
  fs.writeFileSync(pidFile, JSON.stringify({ pid: srv.pid, zipMtime, port: PORT }));
  srv.unref();
  await waitForDumps(shapes, srv.pid);
  finish();
}
