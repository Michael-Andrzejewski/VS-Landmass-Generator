// Measure an exact .lmd dump numerically, instead of looking at it.
//
//   node tools/lmd-probe.mjs <name> water <x> <z> [radius]
//   node tools/lmd-probe.mjs <name> column <x> <z>
//
// x and z are offsets from the ISLAND CENTRE in blocks, which is what the
// shape file thinks in: a cell at (dx,dz) on a 200 grid at diameter 600 is
// (3*dx, 3*dz) here. The previewer renders these dumps but cannot answer "how
// deep is that water", and the answer is the whole point of the exact pass.
//
// Format (see WriteDump): gzip of "LMD1" + int32 headerLen + header JSON +
// runs of (uint32 count, uint16 paletteIdx), iterated y, then z, then x.

import { readFileSync, existsSync } from 'fs';
import { gunzipSync } from 'zlib';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const [name, mode = 'water', xs = '0', zs = '0', rs = '60'] = process.argv.slice(2);
if (!name) { console.error('usage: node tools/lmd-probe.mjs <name> water|column <x> <z> [radius]'); process.exit(1); }
const path = name.endsWith('.lmd') ? name
  : join(root, 'export', 'data', 'LandmassGenerator', 'dumps', name + '.lmd');
if (!existsSync(path)) { console.error(`no dump at ${path}; run: node tools/dumpgen.mjs ${name}`); process.exit(1); }

const raw = gunzipSync(readFileSync(path));
if (raw.toString('ascii', 0, 4) !== 'LMD1') { console.error('not an LMD1 dump'); process.exit(1); }
const hlen = raw.readInt32LE(4);
const h = JSON.parse(raw.toString('utf8', 8, 8 + hlen));
const { ox, oy, oz, sx, sy, sz, sea, cx, cz, palette } = h;
console.log(`${name}: ${sx} x ${sy} x ${sz} at (${ox},${oy},${oz}), sea level ${sea}, centre ${cx},${cz}`);

// "waterlily" also starts with water, hence the trailing dash.
const isWater = palette.map((c) => /^game:(water|saltwater)-/.test(c));
const px = cx + parseInt(xs, 10) - ox, pz = cz + parseInt(zs, 10) - oz;
const R = mode === 'column' ? 0 : parseInt(rs, 10);
const layer = sx * sz;

const want = new Map();
for (let dz = -R; dz <= R; dz++)
  for (let dx = -R; dx <= R; dx++) {
    const col = (pz + dz) * sx + (px + dx);
    if (col >= 0 && col < layer) want.set(col, []);
  }

let p = 8 + hlen, idx = 0;
while (p + 6 <= raw.length) {
  const n = raw.readUInt32LE(p), pi = raw.readUInt16LE(p + 4); p += 6;
  if (isWater[pi] || mode === 'column') {
    for (let k = 0; k < n; k++) {
      const c = idx + k, y = Math.floor(c / layer), col = c - y * layer;
      const a = want.get(col);
      if (a && (mode === 'column' ? pi !== 0 : true)) a.push(mode === 'column' ? [oy + y, pi] : oy + y);
    }
  }
  idx += n;
}

if (mode === 'column') {
  const a = want.get(pz * sx + px) || [];
  a.sort((u, v) => u[0] - v[0]);
  let last = null;
  for (const [y, pi] of a) {
    if (last && palette[pi] === last.code && y === last.to + 1) { last.to = y; continue; }
    last = { code: palette[pi], from: y, to: y }; console.log(`  y${String(y).padStart(4)}  ${palette[pi]}`);
  }
  process.exit(0);
}

// A column can hold the surface water AND a cave pool far below it. Take the
// TOPMOST contiguous run, or a cave reads as a hundred blocks of lake.
const depth = new Map();
for (const [col, ys] of want) {
  if (!ys.length) continue;
  ys.sort((u, v) => u - v);
  let top = ys[ys.length - 1], lo = top;
  for (let i = ys.length - 2; i >= 0 && ys[i] === lo - 1; i--) lo = ys[i];
  if (top < sea + 2) continue;                      // the sea, not an inland pool
  depth.set(col, top - lo + 1);
}
if (!depth.size) { console.log('  no inland water in that window'); process.exit(0); }
let maxD = 0; for (const d of depth.values()) if (d > maxD) maxD = d;
const span = (stepZ) => {
  let best = 0;
  for (let a = -R; a <= R; a++) { let run = 0;
    for (let b = -R; b <= R; b++) {
      const col = stepZ ? (pz + b) * sx + (px + a) : (pz + a) * sx + (px + b);
      run = depth.has(col) ? run + 1 : 0; if (run > best) best = run;
    } }
  return best;
};
const over20 = [...depth.values()].filter((d) => d >= 20).length;
console.log(`  ${span(false)} x ${span(true)} blocks of water, deepest ${maxD}`);
console.log(`  ${depth.size} water columns, ${over20} of them (${Math.round(100 * over20 / depth.size)}%) at least 20 deep`);
const row = [];
for (let dx = -40; dx <= 40; dx += 4) row.push(String(depth.get(pz * sx + (px + dx)) || 0).padStart(3));
console.log(`  section W-E every 4 blocks: ${row.join('')}`);
