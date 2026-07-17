/* Island shape previewer.

   Parses the same shape files /genisland reads and rebuilds the terrain the
   way LandmassGeneratorModSystem.cs does: distance-to-coast height fields,
   smoothed region borders, shore taper, ponds, surface materials. Coastline
   jitter uses a stand-in noise, so the coast DETAIL is approximate; region
   layout, heights and shores are the real math.

   Caves are NOT approximate: CaveRand and carveTunnel below are verbatim
   ports of the C# walk (same xorshift32, same draw order, same constants),
   so the tunnel you see here is the tunnel the game carves. */

'use strict';

// ── deterministic value noise (stand-in for the game's simplex) ──────────
function hash2(ix, iz, seed) {
  let h = Math.imul(ix, 374761393) ^ Math.imul(iz, 668265263) ^ Math.imul(seed, 2246822519);
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}
function vnoise(x, z, seed) {
  const ix = Math.floor(x), iz = Math.floor(z);
  const fx = x - ix, fz = z - iz;
  const sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
  const a = hash2(ix, iz, seed), b = hash2(ix + 1, iz, seed);
  const c = hash2(ix, iz + 1, seed), d = hash2(ix + 1, iz + 1, seed);
  return a + (b - a) * sx + (c - a) * sz + (a - b - c + d) * sx * sz;
}
function fbm(x, z, seed) { // 3 octaves, 0..1
  return (vnoise(x, z, seed) * 4 + vnoise(x * 2, z * 2, seed + 7) * 2 + vnoise(x * 4, z * 4, seed + 13)) / 7;
}

// ── shape file parsing ────────────────────────────────────────────────────
function parseShape(text) {
  const s = { regions: {}, markers: [], caves: [], rows: [], suggested: {} };
  let inMap = false;
  const caveDefs = {}, treeChars = {};
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.replace(/\s+$/, '');
    if (inMap) { if (line.trim().length) s.rows.push(line); continue; }
    const t = line.trim();
    if (!t) continue;
    if (t[0] === '#') {
      const m = t.match(/\/genisland\s+(.*)$/);
      if (m) for (const tok of m[1].split(/\s+/)) {
        const eq = tok.indexOf('=');
        if (eq > 0) s.suggested[tok.slice(0, eq)] = tok.slice(eq + 1);
      }
      continue;
    }
    if (/^map$/i.test(t)) { inMap = true; continue; }
    const tok = t.split(/\s+/);
    if (/^region$/i.test(tok[0]) && tok.length >= 2) {
      const r = {
        key: tok[1][0], rock: 'granite', sand: null, fert: 'medium', surface: 'grass',
        height: 1.0, shore: 8, rough: 0.3, pond: 0, forest: 0, sandy: 0,
      };
      for (let i = 2; i < tok.length; i++) {
        const eq = tok[i].indexOf('='); if (eq <= 0) continue;
        const k = tok[i].slice(0, eq).toLowerCase(), v = tok[i].slice(eq + 1);
        if (k === 'rock') r.rock = v;
        else if (k === 'sand') r.sand = v;
        else if (k === 'fertility') r.fert = v.toLowerCase();
        else if (k === 'surface') r.surface = v.toLowerCase();
        else if (k === 'height') r.height = parseFloat(v) || 1;
        else if (k === 'shore') r.shore = Math.max(1, parseFloat(v) || 8);
        else if (k === 'rough') r.rough = parseFloat(v) || 0;
        else if (k === 'pond') r.pond = Math.max(1, Math.min(40, parseFloat(v) || 3));
        else if (k === 'forest') r.forest = parseFloat(v) || 0;
        else if (k === 'sandy') r.sandy = parseFloat(v) || 0;
      }
      s.regions[r.key] = r;
    } else if (/^tree$/i.test(tok[0]) && tok.length >= 3) {
      treeChars[tok[1][0]] = { type: tok[2], size: parseFloat(tok[3]) || 1.5 };
    } else if (/^cave$/i.test(tok[0]) && tok.length >= 2) {
      // Defaults MUST match ParseCave in the C# mod.
      const d = {
        headingDeg: NaN, dip: 12, length: 80, radius: 2.6, squash: 0.72, weave: 0.5, scale: 1,
        branches: 2, branchDepth: 2, branchLen: 0.5, depth: 60, mouth: 2, entry: 10, seed: 0, oreName: null, oreChance: 0,
      };
      for (let i = 2; i < tok.length; i++) {
        const eq = tok[i].indexOf('='); if (eq <= 0) continue;
        const k = tok[i].slice(0, eq).toLowerCase(), v = tok[i].slice(eq + 1);
        if (k === 'heading') { if (v.toLowerCase() !== 'auto') d.headingDeg = parseFloat(v) || 0; }
        else if (k === 'dip') d.dip = Math.min(60, Math.max(0, parseFloat(v) || 12));
        else if (k === 'length') d.length = Math.min(600, Math.max(8, parseFloat(v) || 80));
        else if (k === 'radius') d.radius = Math.min(8, Math.max(1.2, parseFloat(v) || 2.6));
        else if (k === 'squash') d.squash = Math.min(1.5, Math.max(0.4, parseFloat(v) || 0.72));
        else if (k === 'weave') d.weave = Math.min(1, Math.max(0, parseFloat(v) || 0));
        else if (k === 'scale') d.scale = Math.min(4, Math.max(0.5, parseFloat(v) || 1));
        else if (k === 'branches') d.branches = Math.min(8, Math.max(0, Math.trunc(parseFloat(v) || 2)));
        else if (k === 'branchdepth') d.branchDepth = Math.min(4, Math.max(0, Math.trunc(parseFloat(v) || 2)));
        else if (k === 'branchlen') d.branchLen = Math.min(1.2, Math.max(0.2, parseFloat(v) || 0.5));
        else if (k === 'depth') d.depth = Math.min(200, Math.max(4, parseFloat(v) || 60));
        else if (k === 'mouth') d.mouth = Math.min(30, Math.max(0, Math.trunc(parseFloat(v) || 2)));
        else if (k === 'entry') d.entry = Math.min(60, Math.max(0, Math.trunc(parseFloat(v) || 10)));
        else if (k === 'seed') d.seed = Math.abs(Math.trunc(parseFloat(v) || 0)) >>> 0;
        else if (k === 'ores') { const p = v.split(':'); d.oreName = p[0]; d.oreChance = parseFloat(p[1]) || 0.04; }
      }
      caveDefs[tok[1][0]] = d;
    }
  }

  let w = 0;
  for (const r of s.rows) w = Math.max(w, r.length);
  s.W = w; s.H = s.rows.length;
  s.cells = [];
  for (let z = 0; z < s.H; z++) {
    const row = [];
    for (let x = 0; x < w; x++) {
      let c = x < s.rows[z].length ? s.rows[z][x] : '.';
      if (treeChars[c]) { s.markers.push({ gx: x, gz: z, size: treeChars[c].size }); c = '?'; }
      else if (caveDefs[c]) { s.caves.push({ gx: x, gz: z, def: caveDefs[c] }); c = '?'; }
      row.push(c);
    }
    s.cells.push(row);
  }
  // Marker cells adopt a neighbouring region, like the mod does.
  for (let z = 0; z < s.H; z++)
    for (let x = 0; x < w; x++)
      if (s.cells[z][x] === '?') s.cells[z][x] = neighbourRegion(s, x, z);
  return s;
}

function neighbourRegion(s, x, z) {
  for (let r = 1; r <= 4; r++)
    for (let dz = -r; dz <= r; dz++)
      for (let dx = -r; dx <= r; dx++) {
        const nx = x + dx, nz = z + dz;
        if (nx < 0 || nz < 0 || nx >= s.W || nz >= s.H) continue;
        const c = s.cells[nz][nx];
        if (c !== '.' && c !== '?') return c;
      }
  return '.';
}

// ── the mod's terrain math, ported ────────────────────────────────────────
function distanceField(srcFn, W, H, outsideIsSource) {
  const INF = 1e9;
  const d = new Float32Array(W * H);
  for (let z = 0; z < H; z++) for (let x = 0; x < W; x++) d[z * W + x] = srcFn(x, z) ? 0 : INF;
  const edge = (x, z) => (outsideIsSource ? Math.min(x + 1, W - x, z + 1, H - z) : INF);
  for (let z = 0; z < H; z++)
    for (let x = 0; x < W; x++) {
      let v = Math.min(d[z * W + x], edge(x, z));
      if (x > 0) v = Math.min(v, d[z * W + x - 1] + 1);
      if (z > 0) v = Math.min(v, d[(z - 1) * W + x] + 1);
      if (x > 0 && z > 0) v = Math.min(v, d[(z - 1) * W + x - 1] + 1.414);
      if (x < W - 1 && z > 0) v = Math.min(v, d[(z - 1) * W + x + 1] + 1.414);
      d[z * W + x] = v;
    }
  for (let z = H - 1; z >= 0; z--)
    for (let x = W - 1; x >= 0; x--) {
      let v = d[z * W + x];
      if (x < W - 1) v = Math.min(v, d[z * W + x + 1] + 1);
      if (z < H - 1) v = Math.min(v, d[(z + 1) * W + x] + 1);
      if (x < W - 1 && z < H - 1) v = Math.min(v, d[(z + 1) * W + x + 1] + 1.414);
      if (x > 0 && z < H - 1) v = Math.min(v, d[(z + 1) * W + x - 1] + 1.414);
      d[z * W + x] = v;
    }
  return d;
}

function smoothField(src, known, W, H, passes) {
  let cur = Float32Array.from(src), have = Uint8Array.from(known);
  for (let p = 0; p < passes; p++) {
    const next = Float32Array.from(cur), nextHave = Uint8Array.from(have);
    for (let z = 0; z < H; z++)
      for (let x = 0; x < W; x++) {
        let sum = 0, cnt = 0;
        for (let dz = -1; dz <= 1; dz++)
          for (let dx = -1; dx <= 1; dx++) {
            const nx = x + dx, nz = z + dz;
            if (nx < 0 || nz < 0 || nx >= W || nz >= H || !have[nz * W + nx]) continue;
            sum += cur[nz * W + nx]; cnt++;
          }
        if (cnt > 0) { next[z * W + x] = sum / cnt; nextHave[z * W + x] = 1; }
      }
    cur = next; have = nextHave;
  }
  return cur;
}

function bilinear(f, W, H, gx, gz) {
  const cx = Math.min(Math.max(gx, 0), W - 1.001), cz = Math.min(Math.max(gz, 0), H - 1.001);
  const x0 = Math.floor(cx), z0 = Math.floor(cz);
  const x1 = Math.min(x0 + 1, W - 1), z1 = Math.min(z0 + 1, H - 1);
  const tx = cx - x0, tz = cz - z0;
  const a = f[z0 * W + x0] * (1 - tx) + f[z0 * W + x1] * tx;
  const b = f[z1 * W + x0] * (1 - tx) + f[z1 * W + x1] * tx;
  return a * (1 - tz) + b * tz;
}

const smooth = (t) => { t = Math.min(Math.max(t, 0), 1); return t * t * (3 - 2 * t); };
const lerp = (a, b, t) => a + (b - a) * t;

// The world model: everything in blocks, sea level = y 0 (the water surface
// plane; land flush with it tops out at block -1, exactly like the mod's
// SeaLevel - 1 rule).
function buildIsland(shape, diameter, domeHeight) {
  const W = shape.W, H = shape.H;
  const wpc = diameter / Math.max(W, H);
  const isLand = (x, z) => shape.cells[z][x] !== '.';
  const distToOcean = distanceField((x, z) => !isLand(x, z), W, H, true);
  const distToLand = distanceField(isLand, W, H, false);

  const rawH = new Float32Array(W * H), rawS = new Float32Array(W * H), known = new Uint8Array(W * H);
  for (let z = 0; z < H; z++)
    for (let x = 0; x < W; x++) {
      const c = shape.cells[z][x];
      if (c !== '.' && shape.regions[c]) {
        rawH[z * W + x] = shape.regions[c].height;
        rawS[z * W + x] = shape.regions[c].shore;
        known[z * W + x] = 1;
      }
    }
  const heightField = smoothField(rawH, known, W, H, 5);
  const shoreField = smoothField(rawS, known, W, H, 5);

  const oceanRing = Math.max(24, diameter * 0.22);
  const water = 30; // /genisland default carve depth just off the coast

  function gridPos(x, z) {
    const jx = (fbm(x / 26, z / 26, 1001) - 0.5) * 2 * 0.7;
    const jz = (fbm(x / 26, z / 26, 2002) - 0.5) * 2 * 0.7;
    const gx = x / wpc + W / 2 + jx, gz = z / wpc + H / 2 + jz;
    return { gx, gz, cx: Math.floor(gx), cz: Math.floor(gz) };
  }

  const pondRim = (r) => -1 + Math.round(domeHeight * r.height);

  function pondEdgeDist(gx, gz) {
    const cx = Math.floor(gx), cz = Math.floor(gz);
    let best = 6;
    for (let dz = -6; dz <= 6; dz++)
      for (let dx = -6; dx <= 6; dx++) {
        const nx = cx + dx, nz = cz + dz;
        const pond = nx >= 0 && nz >= 0 && nx < W && nz < H && shape.cells[nz][nx] !== '.'
          && shape.regions[shape.cells[nz][nx]] && shape.regions[shape.cells[nz][nx]].pond > 0;
        if (pond) continue;
        const d = Math.hypot(nx + 0.5 - gx, nz + 0.5 - gz);
        if (d < best) best = d;
      }
    return best;
  }

  function neighbourPond(cx, cz) {
    for (let dz = -1; dz <= 1; dz++)
      for (let dx = -1; dx <= 1; dx++) {
        if (!dx && !dz) continue;
        const nx = cx + dx, nz = cz + dz;
        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
        const c = shape.cells[nz][nx];
        if (c !== '.' && shape.regions[c] && shape.regions[c].pond > 0) return shape.regions[c];
      }
    return null;
  }

  // Mirrors ShapeSurface. Returns null outside the work area.
  function columnSurface(x, z) {
    const { gx, gz, cx, cz } = gridPos(x, z);
    const inGrid = cx >= 0 && cz >= 0 && cx < W && cz < H;
    const cell = inGrid ? shape.cells[cz][cx] : '.';
    const reg = cell !== '.' ? shape.regions[cell] : null;

    if (reg) {
      const dCoast = bilinear(distToOcean, W, H, gx, gz) * wpc;
      const hFrac = bilinear(heightField, W, H, gx, gz);
      const shore = Math.max(1, bilinear(shoreField, W, H, gx, gz));
      const rise = domeHeight * hFrac * smooth(dCoast / shore);
      const rough = (fbm(x / 22, z / 22, 3003) - 0.5) * 2 * reg.rough * 4;
      const bumps = rough * Math.min(1, dCoast / 6);
      let landY = Math.round(-1 + rise) + Math.round(bumps);
      if (landY < -1) landY = -1;

      if (reg.pond > 0) {
        const rimY = pondRim(reg);
        const dEdge = pondEdgeDist(gx, gz) * wpc;
        const depth = 1 + Math.round((reg.pond - 1) * smooth(dEdge / 4));
        return { topY: Math.max(-2, rimY - depth), waterTop: rimY - 1, mat: 'pond', reg, cell };
      }
      const pondN = neighbourPond(cx, cz);
      const topY = pondN ? pondRim(pondN) - 1 : landY;
      let mat = reg.surface;
      if (mat === 'rocksand') mat = fbm(x * 0.9 / 40, z * 0.9 / 40, 4004) > 0.5 ? 'rock' : 'sand';
      if (reg.sandy > 0 && (mat === 'grass' || mat === 'barren')
        && fbm(x * 0.23 / 4, z * 0.23 / 4, 5005) > 1 - reg.sandy * 0.62) mat = 'sand';
      return { topY, waterTop: -1000, mat, reg, cell };
    }

    // Ocean ring.
    const ccx = Math.min(Math.max(gx, 0), W - 1), ccz = Math.min(Math.max(gz, 0), H - 1);
    const over = Math.hypot(gx - ccx, gz - ccz);
    const dLand = (bilinear(distToLand, W, H, ccx, ccz) + over) * wpc;
    if (dLand > oceanRing) return null;
    const naturalY = -8;
    const deep = -2 - water * smooth(dLand / (oceanRing * 0.45));
    const back = smooth((dLand - oceanRing * 0.55) / (oceanRing * 0.45));
    const topY = Math.round(lerp(deep, naturalY, back));
    return { topY, waterTop: topY < 0 ? -1 : -1000, mat: topY >= -4 ? 'sand' : 'rock', reg: null, cell: '.' };
  }

  return { shape, wpc, oceanRing, columnSurface };
}

// Mirror of the mod's open-air scan: walk seaward along the heading for the
// first column open at mouth height (or water); the mouth anchors there.
function findOpenS(island, ex, ez, hor, mouthY) {
  for (let s = 0; s >= -24; s--) {
    const sxc = Math.floor(ex + 0.5 + Math.cos(hor) * s);
    const szc = Math.floor(ez + 0.5 + Math.sin(hor) * s);
    const col = island.columnSurface(sxc, szc);
    const g = col ? col.topY : -999;
    if (g <= mouthY - 1 || g <= -2) return s;
  }
  return 0;
}

// ── cave walk: VERBATIM port of the C# CarveTunnel path logic ─────────────
// Same xorshift32, same draw order, same constants. Do not "improve" this
// side alone; change both or the preview lies.
function CaveRand(seed) {
  let s = (seed >>> 0) === 0 ? 2463534242 : seed >>> 0;
  this.nextUInt = () => {
    s = (s ^ (s << 13)) >>> 0;
    s = (s ^ (s >>> 17)) >>> 0;
    s = (s ^ (s << 5)) >>> 0;
    return s;
  };
  this.nextDouble = () => (this.nextUInt() >>> 8) / 16777216;
}

function traceCaves(island, domeHeight) {
  const { shape, wpc } = island;
  const steps = [], mouths = [];
  const guard = { total: 0 };

  for (const cm of shape.caves) {
    const def = cm.def;
    const ex = Math.round((cm.gx + 0.5 - shape.W / 2) * wpc);
    const ez = Math.round((cm.gz + 0.5 - shape.H / 2) * wpc);
    const col = island.columnSurface(ex, ez);
    if (!col || col.topY < 0) continue;
    const mouthY = Math.max(-1, -1 + def.mouth);

    let hor;
    if (isNaN(def.headingDeg)) hor = Math.atan2(0 - ez, 0 - ex);
    else {
      const th = def.headingDeg * Math.PI / 180;
      hor = Math.atan2(-Math.cos(th), Math.sin(th));
    }
    const seed = def.seed !== 0 ? def.seed
      : (0x9E3779B9 ^ Math.imul(cm.gx, 668265263) ^ Math.imul(cm.gz, 2246822519)) >>> 0;

    const openS = findOpenS(island, ex, ez, hor, mouthY);
    const sx = ex + 0.5 + Math.cos(hor) * (openS - 4), sz = ez + 0.5 + Math.sin(hor) * (openS - 4);
    mouths.push({ x: Math.round(ex + Math.cos(hor) * openS), y: mouthY, z: Math.round(ez + Math.sin(hor) * openS) });
    walk(def, sx, mouthY + 1.6, sz, hor, def.dip * Math.PI / 180, Math.trunc(def.length) + 4,
      def.radius * def.scale, mouthY + 1.6 - def.depth, def.branches, def.branchDepth, new CaveRand(seed), 0);
  }

  // Emulates the carver's fluid guard: a step whose padded ellipsoid reaches
  // any water block is SKIPPED in-game, so the preview marks it (dark) rather
  // than pretending it gets carved. Sampled at 9 columns, close enough.
  function touchesWater(x, y, z, hr, vr) {
    for (let k = 0; k < 9; k++) {
      const a = (k / 9) * Math.PI * 2;
      const sx = k === 0 ? x : x + Math.cos(a) * (hr + 1);
      const sz = k === 0 ? z : z + Math.sin(a) * (hr + 1);
      const col = island.columnSurface(Math.round(sx), Math.round(sz));
      if (!col) continue;
      const wTop = col.waterTop > col.topY ? col.waterTop : -1000;
      if (wTop <= -1000) continue;
      if (y + vr + 1 >= col.topY + 1 && y - vr - 1 <= wTop + 1) return true;
    }
    return false;
  }

  function walk(def, x, y, z, hor, dip, length, radius, floorY, branches, branchDepth, rand, level) {
    const path = [];
    // level 0 is the main tunnel: it leaves the mouth dead level for the
    // entry adit (7 doorway steps + def.entry), mirroring the C# mouthSteps.
    let mh = 0, mv = 0, pulse = 0, vert = level === 0 ? 0 : -dip * 0.5;
    let hswell = 0, vswell = 0;
    const hor0 = hor;
    const homing = 0.03 + 0.05 * (1 - def.weave);
    for (let i = 0; i < length; i++) {
      if (guard.total++ > 8000) break;
      const t = i / length;
      const u1 = rand.nextDouble(), u2 = rand.nextDouble();
      const u3 = rand.nextDouble(), u4 = rand.nextDouble();
      const u5 = rand.nextDouble();
      const u7 = rand.nextDouble(), u8 = rand.nextDouble();
      const u9 = rand.nextDouble();
      mh = 0.9 * mh + (u1 * 2 - 1) * u2;
      hor += def.weave * 0.25 * mh;
      if (u5 < 0.018) hor += (rand.nextDouble() - 0.5) * (Math.PI / 2);
      hor += Math.atan2(Math.sin(hor0 - hor), Math.cos(hor0 - hor)) * homing;
      mv = 0.9 * mv + (u3 * 2 - 1) * u4;
      vert += def.weave * 0.05 * mv;
      pulse = 0.9 * pulse + (u7 * 2 - 1) * u8;
      hswell *= 0.92;
      vswell *= 0.92;
      if (u9 < 0.011) {
        const u10 = rand.nextDouble(); // always drawn: keeps seeds stable
        if (!(level === 0 && i < 7 + def.entry + 10)) {
          const deepFrac = Math.min(Math.max(1 - (y - floorY) / Math.max(8, def.depth), 0), 1);
          const boost = (0.8 + u10 * 2.2) * def.scale * (0.6 + 1.4 * deepFrac);
          hswell += boost;
          vswell += boost * 0.45;
        }
      }
      const target = level === 0 && i < 7 + def.entry ? 0 : (y > floorY ? -dip : 0);
      vert += (target - vert) * 0.12;
      vert = Math.min(Math.max(vert, -0.85), 0.3);
      const cv = Math.cos(vert);
      x += Math.cos(hor) * cv;
      z += Math.sin(hor) * cv;
      y += Math.sin(vert);
      if (y < -102) y = -102; // the C# clamp at absolute y=8 (sea level ~110)
      const r = Math.min(13, Math.max(1.5, radius * (0.7 + 0.6 * Math.sin(t * Math.PI)) + pulse * 0.9 + hswell));
      const v = Math.min(10, Math.max(1.45, r * def.squash + vswell * 0.5));
      steps.push({ x, y, z, r, v, level, wet: touchesWater(x, y, z, r, v) });
      path.push({ x, y, z, hor });
    }
    if (branchDepth <= 0 || path.length < 20) return;
    for (let b = 0; b < branches; b++) {
      const f = 0.25 + rand.nextDouble() * 0.6;
      const side = rand.nextDouble() < 0.5 ? -1 : 1;
      const angOff = side * (0.7 + rand.nextDouble() * 0.8);
      const lenFrac = def.branchLen * (0.7 + rand.nextDouble() * 0.6);
      const childSeed = rand.nextUInt();
      const p = path[Math.min(Math.max(Math.trunc(f * path.length), 0), path.length - 1)];
      walk(def, p.x, p.y, p.z, p.hor + angOff, dip * 0.75, Math.trunc(length * lenFrac),
        radius * 0.85, floorY, Math.max(1, branches - 1), branchDepth - 1, new CaveRand(childSeed), level + 1);
    }
  }

  return { steps, mouths };
}

// ── colors ────────────────────────────────────────────────────────────────
const ROCK = { slate: 0x6e7683, granite: 0x8d8378, basalt: 0x3d3d46, peridotite: 0x5d6b58, andesite: 0x7c8288, chalk: 0xd8d8cc, limestone: 0xb8b09a };
const SANDC = { chalk: 0xe8e2c8, slate: 0x9aa0a8, basalt: 0x4a4a52, peridotite: 0x8a9478, granite: 0xcfc49a, andesite: 0xa8a89a };
const GRASS = { verylow: 0x8a9455, low: 0x7f9c4e, medium: 0x64a03e, high: 0x4f9c35, compost: 0x4f9c35, terrapreta: 0x3f8c30 };

function columnColor(col, x, z, arid) {
  const r = col.reg;
  if (col.mat === 'pond') return 0x6a5a40;
  if (col.mat === 'sand') {
    const code = r && r.sand ? r.sand.replace(/^sand-/, '') : (r ? r.rock : 'granite');
    return SANDC[code] !== undefined ? SANDC[code] : 0xcfc49a;
  }
  if (col.mat === 'rock') return ROCK[r ? r.rock : 'granite'] || 0x808080;
  if (col.mat === 'barren') return (x * 7 + z * 13) % 3 ? 0x9a8054 : 0x8a7a50;
  let g = GRASS[r ? r.fert : 'medium'] || GRASS.medium;
  if (arid) { // rusty desert fade, like climate=arid
    const c = new THREE.Color(g);
    c.lerp(new THREE.Color(0xb59a45), 0.65);
    g = c.getHex();
  }
  return g;
}

// ── three.js scene ────────────────────────────────────────────────────────
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setSize(window.innerWidth, window.innerHeight);
document.body.appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0a1a26);
const camera = new THREE.PerspectiveCamera(55, window.innerWidth / window.innerHeight, 0.1, 4000);
scene.add(new THREE.HemisphereLight(0xdff0ff, 0x35485a, 0.45));
scene.add(new THREE.AmbientLight(0xffffff, 0.28));
const d1 = new THREE.DirectionalLight(0xffffff, 0.65); d1.position.set(0.5, 1, 0.35); scene.add(d1);
const d2 = new THREE.DirectionalLight(0xbcd6ee, 0.3); d2.position.set(-0.5, 0.4, -0.6); scene.add(d2);

let group = null;
let caveMats = [];
const boxGeo = new THREE.BoxGeometry(1, 1, 1);

function rebuild(shape, dia, hgt) {
  if (group) { scene.remove(group); group.traverse((o) => { if (o.geometry && o.geometry !== boxGeo) o.geometry.dispose(); if (o.material) o.material.dispose(); }); }
  group = new THREE.Group();
  caveMats = [];

  const island = buildIsland(shape, dia, hgt);
  const arid = (shape.suggested.climate || '') === 'arid' || (shape.suggested.climate || '') === 'dry';
  const half = Math.ceil(Math.max(shape.W, shape.H) * island.wpc / 2) + 16;

  // Cave-mouth headwalls: mirror of the mod's StampHeadwall, a stone
  // outcrop around each entrance that the adit bores into.
  const headwalls = new Map();
  for (const cm of shape.caves) {
    const def = cm.def;
    const ex = Math.round((cm.gx + 0.5 - shape.W / 2) * island.wpc);
    const ez = Math.round((cm.gz + 0.5 - shape.H / 2) * island.wpc);
    const col0 = island.columnSurface(ex, ez);
    if (!col0 || col0.topY < 0) continue;
    const mouthY = Math.max(-1, -1 + def.mouth);
    let hor;
    if (isNaN(def.headingDeg)) hor = Math.atan2(0 - ez, 0 - ex);
    else { const th = def.headingDeg * Math.PI / 180; hor = Math.atan2(-Math.cos(th), Math.sin(th)); }
    const r0 = Math.max(1.5, def.radius * def.scale * 0.7);
    const v0 = Math.max(1.45, r0 * def.squash);
    const cy0 = mouthY + 1.6, rw = r0 + 3;
    const dirx = Math.cos(hor), dirz = Math.sin(hor);
    const openS = findOpenS(island, ex, ez, hor, mouthY);
    const reach = Math.ceil(18 + Math.abs(openS) + rw);
    for (let zz = ez - reach; zz <= ez + reach; zz++)
      for (let xx = ex - reach; xx <= ex + reach; xx++) {
        const ox = xx - ex, oz = zz - ez;
        const s = ox * dirx + oz * dirz, q = -ox * dirz + oz * dirx;
        if (s < openS - 1 || s > openS + 18 || Math.abs(q) > rw) continue;
        const c2 = island.columnSurface(xx, zz);
        if (!c2 || c2.topY <= -2) continue;
        const shoulder = (q / rw) * (q / rw);
        const top = Math.round(cy0 + v0 + 2 - 2.5 * shoulder - Math.max(0, openS + 1 - s) * 1.2);
        // >= : even an equal-height column gets its surface turned to rock.
        if (top >= c2.topY) headwalls.set(xx + ',' + zz, top);
      }
  }

  // Terrain columns.
  const cols = [];
  let minTop = 0;
  for (let z = -half; z <= half; z++)
    for (let x = -half; x <= half; x++) {
      const col = island.columnSurface(x, z);
      if (!col) continue;
      cols.push({ x, z, col });
      if (col.topY < minTop) minTop = col.topY;
    }
  const base = minTop - 2;

  const terr = new THREE.InstancedMesh(boxGeo, new THREE.MeshLambertMaterial(), cols.length);
  const dummy = new THREE.Object3D();
  const c3 = new THREE.Color();
  const pondWater = [];
  const forestTrees = [];
  for (let i = 0; i < cols.length; i++) {
    const { x, z, col } = cols[i];
    const hw = headwalls.get(x + ',' + z);
    const topY = hw !== undefined ? Math.max(hw, col.topY) : col.topY;
    const hgtY = topY + 1 - base;
    dummy.position.set(x, base + hgtY / 2, z);
    dummy.scale.set(1, hgtY, 1);
    dummy.updateMatrix();
    terr.setMatrixAt(i, dummy.matrix);
    terr.setColorAt(i, c3.setHex(hw !== undefined && hw >= col.topY
      ? (ROCK[col.reg ? col.reg.rock : 'granite'] || 0x808080)
      : columnColor(col, x, z, arid)));
    if (col.mat === 'pond' && col.waterTop > col.topY) pondWater.push({ x, z, y: col.waterTop });
    if (col.reg && col.reg.forest > 0 && col.mat === 'grass'
      && hash2(x, z, 6006) < col.reg.forest) forestTrees.push({ x, z, y: col.topY });
  }
  terr.instanceMatrix.needsUpdate = true;
  group.add(terr);

  // Water: one translucent ocean slab, plus pond levels.
  const waterMat = new THREE.MeshLambertMaterial({ color: 0x2a6a9a, transparent: true, opacity: 0.55, depthWrite: false });
  const ocean = new THREE.Mesh(new THREE.BoxGeometry(half * 2, 0.2, half * 2), waterMat);
  ocean.position.set(0, -0.12, 0);
  ocean.name = 'water';
  group.add(ocean);
  if (pondWater.length) {
    const pw = new THREE.InstancedMesh(boxGeo, waterMat.clone(), pondWater.length);
    for (let i = 0; i < pondWater.length; i++) {
      dummy.position.set(pondWater[i].x, pondWater[i].y + 0.85, pondWater[i].z);
      dummy.scale.set(1, 0.3, 1);
      dummy.updateMatrix();
      pw.setMatrixAt(i, dummy.matrix);
    }
    pw.name = 'water';
    group.add(pw);
  }

  // Trees: landmark cones + forest sprinkle.
  const markCol = new THREE.MeshLambertMaterial({ color: 0x2f7a2f });
  for (const m of shape.markers) {
    const mx = Math.round((m.gx + 0.5 - shape.W / 2) * island.wpc);
    const mz = Math.round((m.gz + 0.5 - shape.H / 2) * island.wpc);
    const col = island.columnSurface(mx, mz);
    if (!col) continue;
    const h = 7 * m.size;
    const cone = new THREE.Mesh(new THREE.ConeGeometry(3.2 * m.size, h, 8), markCol);
    cone.position.set(mx, col.topY + 1 + h / 2, mz);
    group.add(cone);
  }
  if (forestTrees.length) {
    const ft = new THREE.InstancedMesh(boxGeo, new THREE.MeshLambertMaterial({ color: 0x3a7a33 }), forestTrees.length);
    for (let i = 0; i < forestTrees.length; i++) {
      dummy.position.set(forestTrees[i].x, forestTrees[i].y + 3.4, forestTrees[i].z);
      dummy.scale.set(2.6, 4.4, 2.6);
      dummy.updateMatrix();
      ft.setMatrixAt(i, dummy.matrix);
    }
    group.add(ft);
  }

  // Caves: the true in-game path.
  const caves = traceCaves(island, hgt);
  const LEVELC = [0xff7a22, 0xffb347, 0xffe08a, 0xfff2c0];
  for (let lv = -1; lv < 4; lv++) {
    // lv -1 collects steps the in-game fluid guard would SKIP (they touch
    // water), drawn dark so a cave leaking out of the island is obvious.
    const lvSteps = caves.steps.filter((s) => (lv === -1 ? s.wet : s.level === lv && !s.wet));
    if (!lvSteps.length) continue;
    const mat = new THREE.MeshLambertMaterial({
      color: lv === -1 ? 0x4a3038 : LEVELC[lv],
      transparent: true, opacity: lv === -1 ? 0.4 : 0.6, depthTest: true,
    });
    caveMats.push(mat);
    const cm = new THREE.InstancedMesh(new THREE.SphereGeometry(0.5, 6, 5), mat, lvSteps.length);
    for (let i = 0; i < lvSteps.length; i++) {
      const st = lvSteps[i];
      dummy.position.set(st.x, st.y, st.z);
      dummy.scale.set(st.r * 2, st.v * 2, st.r * 2);
      dummy.updateMatrix();
      cm.setMatrixAt(i, dummy.matrix);
    }
    cm.renderOrder = 5;
    group.add(cm);
  }
  for (const m of caves.mouths) {
    const s = new THREE.Mesh(new THREE.SphereGeometry(2.2, 10, 8),
      new THREE.MeshLambertMaterial({ color: 0x33ff77, transparent: true, opacity: 0.9 }));
    s.position.set(m.x, m.y + 1.5, m.z);
    s.renderOrder = 6;
    caveMats.push(s.material);
    group.add(s);
  }

  scene.add(group);
  applyToggles();

  let deepest = 0, wet = 0;
  for (const st of caves.steps) { if (st.y < deepest) deepest = st.y; if (st.wet) wet++; }
  return {
    columns: cols.length,
    caveSteps: caves.steps.length,
    caveWet: wet,
    caveMouths: caves.mouths.length,
    caveDeepest: Math.round(-deepest),
    size: half * 2,
  };
}

function applyToggles() {
  const xray = document.getElementById('xray').checked;
  const wtr = document.getElementById('wtr').checked;
  for (const m of caveMats) { m.depthTest = !xray; m.needsUpdate = true; }
  if (group) group.traverse((o) => { if (o.name === 'water') o.visible = wtr; });
  render();
}

// ── orbit controls ────────────────────────────────────────────────────────
let az = 0.9, pol = 0.95, rad = 260;
// The window can report a 0-size viewport at load (embedded panes do this),
// so every render self-heals the canvas size instead of trusting load time.
function fitRenderer() {
  const w = Math.max(320, window.innerWidth), h = Math.max(240, window.innerHeight);
  if (renderer.domElement.width !== w || renderer.domElement.height !== h) {
    renderer.setSize(w, h);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
}
function render() { fitRenderer(); renderer.render(scene, camera); }
function updateCam() {
  const sp = Math.sin(pol);
  camera.position.set(rad * sp * Math.cos(az), rad * Math.cos(pol), rad * sp * Math.sin(az));
  camera.lookAt(0, 0, 0);
  render();
}
let drag = false, px = 0, py = 0;
renderer.domElement.addEventListener('pointerdown', (e) => { drag = true; px = e.clientX; py = e.clientY; });
addEventListener('pointerup', () => { drag = false; });
addEventListener('pointermove', (e) => {
  if (!drag) return;
  az += (e.clientX - px) * 0.006;
  pol = Math.max(0.08, Math.min(3.06, pol - (e.clientY - py) * 0.006));
  px = e.clientX; py = e.clientY; updateCam();
});
renderer.domElement.addEventListener('wheel', (e) => {
  e.preventDefault();
  rad = Math.max(10, Math.min(1500, rad * (1 + Math.sign(e.deltaY) * 0.08)));
  updateCam();
}, { passive: false });
addEventListener('resize', render);

// ── UI wiring ─────────────────────────────────────────────────────────────
const sel = document.getElementById('sel');
const info = document.getElementById('info');
const legend = document.getElementById('legend');
let currentShape = null;

function refresh() {
  if (!currentShape) return;
  const dia = parseInt(document.getElementById('dia').value, 10) || 150;
  const hgt = parseInt(document.getElementById('hgt').value, 10) || 8;
  const stats = rebuild(currentShape, dia, hgt);
  rad = Math.max(rad, stats.size * 1.1);
  updateCam();
  info.textContent = `${sel.value}: ${stats.columns.toLocaleString()} columns`
    + (stats.caveMouths
      ? `\ncaves: ${stats.caveMouths} mouth(s), ${stats.caveSteps} steps, deepest ${stats.caveDeepest} below sea`
        + (stats.caveWet ? `\n${stats.caveWet} step(s) touch water and will NOT carve (dark)` : '')
      : '\nno caves declared');

  legend.innerHTML = '';
  for (const k in currentShape.regions) {
    const r = currentShape.regions[k];
    const col = columnColor({ mat: r.surface === 'rocksand' ? 'rock' : (r.pond ? 'pond' : r.surface), reg: r }, 0, 0,
      (currentShape.suggested.climate || '') === 'arid');
    legend.innerHTML += `<span class="sw" style="background:#${col.toString(16).padStart(6, '0')}"></span>`
      + `${k}: ${r.surface}${r.pond ? ' pond' : ''} h=${r.height}<br>`;
  }
}

async function load(name) {
  info.textContent = 'loading ' + name + '...';
  try {
    const res = await fetch('/shapes/' + name + '.txt');
    if (!res.ok) throw new Error(res.status);
    currentShape = parseShape(await res.text());
    if (currentShape.suggested.diameter) document.getElementById('dia').value = currentShape.suggested.diameter;
    if (currentShape.suggested.height) document.getElementById('hgt').value = currentShape.suggested.height;
    refresh();
  } catch (e) {
    info.textContent = 'failed to load ' + name + ': ' + e.message;
  }
}

sel.addEventListener('change', () => load(sel.value));
document.getElementById('dia').addEventListener('change', refresh);
document.getElementById('hgt').addEventListener('change', refresh);
document.getElementById('xray').addEventListener('change', applyToggles);
document.getElementById('wtr').addEventListener('change', applyToggles);

(async () => {
  const names = await (await fetch('/list')).json();
  for (const n of names) { const o = document.createElement('option'); o.value = n; o.textContent = n; sel.appendChild(o); }
  const start = names.includes('starter_island') ? 'starter_island' : names[0];
  sel.value = start;
  updateCam();
  if (start) load(start);
})();
