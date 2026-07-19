/* Generic voxel viewer for block dumps produced by
   NadiyaVillageAssistance/tools/schematic_to_voxels.py.
   ?file=data/<name>.json (default data/nadiya-village.json)

   Drag = orbit, wheel = zoom, right-drag = pan, y-slice slider to cut roofs
   off, hover to identify block codes, capture button saves a PNG to
   viewer/shots/ via the existing /capture endpoint. */

'use strict';

const params = new URLSearchParams(location.search);
const FILE = params.get('file') || 'data/nadiya-village.json';

const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
document.body.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x202830);
const camera = new THREE.PerspectiveCamera(55, 1, 0.5, 6000);

scene.add(new THREE.AmbientLight(0xffffff, 0.55));
const sun = new THREE.DirectionalLight(0xfff4e0, 0.9);
sun.position.set(0.7, 1, 0.4);
scene.add(sun);

let mesh = null, data = null, keptIndex = null;

fetch(FILE).then((r) => r.json()).then((d) => {
  data = d;
  document.getElementById('title').textContent = d.name;
  const ycap = document.getElementById('ycap');
  ycap.max = d.size[1];
  ycap.value = d.size[1];
  build(d.size[1]);
  ycap.addEventListener('input', () => build(parseInt(ycap.value, 10)));

  const diag = Math.hypot(d.size[0], d.size[2]);
  dist = diag * 1.05;
  center.set(d.size[0] / 2, d.size[1] * 0.3, d.size[2] / 2);
});

function build(maxY) {
  document.getElementById('ycapval').textContent = 'y ≤ ' + maxY;
  if (mesh) { scene.remove(mesh); mesh.dispose(); }

  const n = data.ci.length;
  let count = 0;
  keptIndex = [];
  for (let i = 0; i < n; i++) if (data.xyz[i * 3 + 1] < maxY) { count++; }

  mesh = new THREE.InstancedMesh(
    new THREE.BoxGeometry(1, 1, 1), new THREE.MeshLambertMaterial(), count);
  const m = new THREE.Matrix4();
  const col = new THREE.Color();
  let j = 0;
  for (let i = 0; i < n; i++) {
    const y = data.xyz[i * 3 + 1];
    if (y >= maxY) continue;
    m.setPosition(data.xyz[i * 3] + 0.5, y + 0.5, data.xyz[i * 3 + 2] + 0.5);
    mesh.setMatrixAt(j, m);
    mesh.setColorAt(j, col.set(data.colors[data.ci[i]]));
    keptIndex[j] = i;
    j++;
  }
  mesh.instanceMatrix.needsUpdate = true;
  if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
  scene.add(mesh);
  document.getElementById('stats').textContent = count.toLocaleString() + ' blocks';
}

// ── controls: orbit / zoom / pan ─────────────────────────────────────────
const center = new THREE.Vector3(100, 15, 110);
let dist = 320, yaw = 0.8, pitch = 0.9;
let dragBtn = -1, px = 0, py = 0;

renderer.domElement.addEventListener('mousedown', (e) => { dragBtn = e.button; px = e.clientX; py = e.clientY; });
window.addEventListener('mouseup', () => (dragBtn = -1));
renderer.domElement.addEventListener('contextmenu', (e) => e.preventDefault());
window.addEventListener('mousemove', (e) => {
  const dx = e.clientX - px, dy = e.clientY - py;
  if (dragBtn === 0) {
    yaw -= dx * 0.005;
    pitch = Math.max(0.05, Math.min(1.5, pitch + dy * 0.005));
    px = e.clientX; py = e.clientY;
  } else if (dragBtn === 2) {
    const s = dist * 0.0012;
    const right = new THREE.Vector3(Math.cos(yaw), 0, -Math.sin(yaw));
    const fwd = new THREE.Vector3(-Math.sin(yaw), 0, -Math.cos(yaw));
    center.addScaledVector(right, -dx * s).addScaledVector(fwd, dy * s);
    px = e.clientX; py = e.clientY;
  } else {
    hover(e);
  }
});
renderer.domElement.addEventListener('wheel', (e) => {
  e.preventDefault();
  dist = Math.max(10, Math.min(3000, dist * (e.deltaY > 0 ? 1.12 : 0.89)));
}, { passive: false });

// ── hover identification ─────────────────────────────────────────────────
const ray = new THREE.Raycaster();
const mouse = new THREE.Vector2();
function hover(e) {
  if (!mesh || !data) return;
  mouse.set((e.clientX / window.innerWidth) * 2 - 1, -(e.clientY / window.innerHeight) * 2 + 1);
  ray.setFromCamera(mouse, camera);
  const hit = ray.intersectObject(mesh)[0];
  const el = document.getElementById('pick');
  if (!hit || hit.instanceId === undefined) { el.textContent = ''; return; }
  const i = keptIndex[hit.instanceId];
  el.textContent = data.codes[data.ci[i]]
    + '  @ ' + data.xyz[i * 3] + ',' + data.xyz[i * 3 + 1] + ',' + data.xyz[i * 3 + 2];
}

// ── capture ──────────────────────────────────────────────────────────────
document.getElementById('cap').addEventListener('click', () => {
  render();
  fetch('/capture?name=' + encodeURIComponent((data ? data.name : 'voxels') + '-' + Date.now()), {
    method: 'POST', body: renderer.domElement.toDataURL('image/png'),
  });
});

// ── render loop ──────────────────────────────────────────────────────────
function fitRenderer() {
  const w = window.innerWidth, h = window.innerHeight;
  if (renderer.domElement.width !== w || renderer.domElement.height !== h) {
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
}
function render() {
  fitRenderer();
  camera.position.set(
    center.x + dist * Math.cos(pitch) * Math.sin(yaw),
    center.y + dist * Math.sin(pitch),
    center.z + dist * Math.cos(pitch) * Math.cos(yaw));
  camera.lookAt(center);
  renderer.render(scene, camera);
}
(function loop() { render(); requestAnimationFrame(loop); })();
