import * as THREE from 'three';
import { OrbitControls } from '../vendor/OrbitControls.js';
import { TransformControls } from '../vendor/TransformControls.js';
import { initOcct, readStepFromUrl, buildFaceTable, buildDisplayMeshes, disposeFaceTable } from './occt.js';
import { traceRaysBrep } from './brepTracer.js';
import { emissionDirections, wavelengthToRGB, buildRayLines } from './optics.js';
import { iorAt, defaultMaterial, PRESETS, TYPE_FIELDS } from './materials.js';

const $ = (id) => document.getElementById(id);
const viewport = $('viewport');

// ------------------------------------------------------------ renderer/scene

const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(window.devicePixelRatio);
renderer.setSize(viewport.clientWidth, viewport.clientHeight);
viewport.appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0b0e12);

const camera = new THREE.PerspectiveCamera(
  50, viewport.clientWidth / viewport.clientHeight, 0.1, 100000
);
camera.position.set(80, 60, 80);

const orbit = new OrbitControls(camera, renderer.domElement);
orbit.enableDamping = true;
orbit.dampingFactor = 0.1;

scene.add(new THREE.HemisphereLight(0x8899aa, 0x223344, 0.9));
const keyLight = new THREE.DirectionalLight(0xffffff, 1.2);
keyLight.position.set(100, 150, 80);
scene.add(keyLight);

const grid = new THREE.GridHelper(200, 40, 0x334455, 0x1e2630);
scene.add(grid);
const axes = new THREE.AxesHelper(20);
scene.add(axes);

// ------------------------------------------------------------- light source

const lightGroup = new THREE.Group();
const lightMarker = new THREE.Mesh(
  new THREE.SphereGeometry(1, 24, 16),
  new THREE.MeshBasicMaterial({ color: 0xffffcc })
);
const glow = new THREE.PointLight(0xffffff, 0, 400);
lightGroup.add(lightMarker, glow);
lightGroup.position.set(50, 10, 0);
scene.add(lightGroup);

const gizmo = new TransformControls(camera, renderer.domElement);
gizmo.attach(lightGroup);
gizmo.setSize(0.8);
scene.add(gizmo);
gizmo.addEventListener('dragging-changed', (e) => { orbit.enabled = !e.value; });
gizmo.addEventListener('objectChange', () => {
  syncLightInputs();
  requestTrace();
});

// aim target for the custom-direction cone: the cone always points from the
// light toward this marker (like a spotlight target)
const aimTarget = new THREE.Mesh(
  new THREE.OctahedronGeometry(1),
  new THREE.MeshBasicMaterial({ color: 0xff8844, wireframe: true })
);
aimTarget.position.set(0, 0, 0);
scene.add(aimTarget);

const aimGizmo = new TransformControls(camera, renderer.domElement);
aimGizmo.attach(aimTarget);
aimGizmo.setSize(0.6);
scene.add(aimGizmo);
aimGizmo.addEventListener('dragging-changed', (e) => { orbit.enabled = !e.value; });
aimGizmo.addEventListener('objectChange', () => {
  syncAimInputs();
  requestTrace();
});

// single place that decides which control widgets are visible; the master
// "Show control widgets" toggle overrides the individual checkboxes
function updateWidgetVisibility() {
  const master = $('show-widgets').checked;
  const lightOn = master && $('light-gizmo').checked;
  gizmo.visible = lightOn;
  gizmo.enabled = lightOn;
  const custom = $('emission-mode').value === 'custom';
  aimTarget.visible = master && custom;
  const aimOn = master && custom && $('aim-gizmo').checked;
  aimGizmo.visible = aimOn;
  aimGizmo.enabled = aimOn;
}

// ------------------------------------------------------------------- state

let oc = null;           // OpenCascade instance (loaded once)
let shape = null;         // current TopoDS_Shape
let faceTable = null;     // { faces, bodies } from occt.buildFaceTable — faces carry the
                           // long-lived analytic surface/classifier/transform handles the
                           // tracer reads directly; bodies carry a mutable `.material`
const faceMeshes = new Map(); // faceId -> THREE.Mesh (display only)
let modelGroup = new THREE.Group();
scene.add(modelGroup);

let rayLines = null;      // current LineSegments of traced rays
let selectedFaceId = null;
let sceneDiag = 100;      // bounding diagonal, drives ray length / epsilon

const NORMAL_MATERIAL = new THREE.MeshStandardMaterial({
  color: 0x7fb8d8, metalness: 0.05, roughness: 0.25,
  transparent: true, opacity: 0.35, side: THREE.DoubleSide, depthWrite: false,
});
const HIGHLIGHT_MATERIAL = new THREE.MeshBasicMaterial({
  color: 0xffaa33, side: THREE.DoubleSide, transparent: true, opacity: 0.55,
  depthWrite: false, polygonOffset: true, polygonOffsetFactor: -2,
});

// --------------------------------------------------------------- UI helpers

function rayCountFromSlider(s) {
  return Math.round(10 * Math.pow(2000, s / 100)); // 10 .. 20,000 (log scale)
}

function currentParams() {
  return {
    lightMode: $('light-mode').value,
    wavelength: Number($('wavelength').value),
    specMin: Number($('spec-min').value),
    specMax: Number($('spec-max').value),
    specSamples: Number($('spec-samples').value),
    intensity: Math.max(0, Number($('intensity-num').value) || 0),
    rayCount: rayCountFromSlider(Number($('ray-count').value)),
    emissionMode: $('emission-mode').value,
    coneAngle: Number($('cone-angle').value),
    maxBounces: Number($('max-bounces').value),
    minIntensity: Number($('min-intensity').value),
    ambientIor: Math.max(1, Number($('ambient-ior').value) || 1),
  };
}

function traceWavelengths(p) {
  if (p.lightMode !== 'spectrum') return [p.wavelength];
  const lo = Math.min(p.specMin, p.specMax);
  const hi = Math.max(p.specMin, p.specMax);
  const list = [];
  for (let i = 0; i < p.specSamples; i++) {
    list.push(lo + (hi - lo) * (p.specSamples === 1 ? 0.5 : i / (p.specSamples - 1)));
  }
  return list;
}

function centerWavelength(p) {
  return p.lightMode === 'spectrum' ? (p.specMin + p.specMax) / 2 : p.wavelength;
}

function cssColor(rgb) {
  return `rgb(${Math.round(rgb[0] * 255)}, ${Math.round(rgb[1] * 255)}, ${Math.round(rgb[2] * 255)})`;
}

function refreshValueLabels() {
  const p = currentParams();
  $('wavelength-val').textContent = p.wavelength;
  $('ray-count-val').textContent = p.rayCount;
  $('cone-angle-val').textContent = p.coneAngle;
  $('max-bounces-val').textContent = p.maxBounces;
  $('min-intensity-val').textContent = p.minIntensity.toFixed(3);
  $('spec-samples-val').textContent = p.specSamples;
  $('wl-swatch').style.background = cssColor(wavelengthToRGB(p.wavelength));
  $('cone-row').style.display = p.emissionMode !== 'sphere' ? 'flex' : 'none';
  $('aim-rows').hidden = p.emissionMode !== 'custom';
  updateWidgetVisibility();
  $('single-wl-rows').hidden = p.lightMode !== 'single';
  $('spectrum-rows').hidden = p.lightMode !== 'spectrum';
  if (p.lightMode === 'spectrum') {
    const stops = [];
    for (let i = 0; i <= 10; i++) {
      const wl = p.specMin + (p.specMax - p.specMin) * (i / 10);
      stops.push(`${cssColor(wavelengthToRGB(wl))} ${i * 10}%`);
    }
    $('spec-swatch').style.background = `linear-gradient(to right, ${stops.join(', ')})`;
  }
  updateIorHints();
}

function updateIorHints() {
  if (!faceTable) return;
  const wl = centerWavelength(currentParams());
  for (const el of document.querySelectorAll('#body-list .n-hint')) {
    const body = faceTable.bodies.find((b) => String(b.id) === el.dataset.bodyId);
    if (body) el.textContent = `n(${Math.round(wl)} nm) = ${iorAt(body.material, wl).toFixed(4)}`;
  }
}

function syncLightInputs() {
  $('light-x').value = lightGroup.position.x.toFixed(1);
  $('light-y').value = lightGroup.position.y.toFixed(1);
  $('light-z').value = lightGroup.position.z.toFixed(1);
}

function syncAimInputs() {
  $('aim-x').value = aimTarget.position.x.toFixed(1);
  $('aim-y').value = aimTarget.position.y.toFixed(1);
  $('aim-z').value = aimTarget.position.z.toFixed(1);
}

function modelCenter() {
  if (faceMeshes.size === 0) return new THREE.Vector3(0, 0, 0);
  const box = new THREE.Box3().setFromObject(modelGroup);
  return box.getCenter(new THREE.Vector3());
}

function updateSceneScale() {
  const box = new THREE.Box3().setFromObject(modelGroup);
  box.expandByPoint(lightGroup.position);
  if (box.isEmpty()) {
    sceneDiag = 100;
  } else {
    sceneDiag = Math.max(box.getSize(new THREE.Vector3()).length(), 10);
  }
  lightMarker.scale.setScalar(Math.max(sceneDiag * 0.008, 0.3));
  aimTarget.scale.setScalar(Math.max(sceneDiag * 0.012, 0.4));
}

function fitView() {
  updateSceneScale();
  const center = faceMeshes.size > 0 ? modelCenter() : lightGroup.position.clone();
  const dist = sceneDiag * 1.2;
  orbit.target.copy(center);
  const dir = camera.position.clone().sub(orbit.target).normalize();
  if (!isFinite(dir.length()) || dir.length() === 0) dir.set(1, 0.6, 1).normalize();
  camera.position.copy(center).addScaledVector(dir, dist);
  camera.near = Math.max(dist / 1000, 0.01);
  camera.far = dist * 100;
  camera.updateProjectionMatrix();
}

// ---------------------------------------------------------------- body list

const CUSTOM_TYPES = { constant: 'Constant n', cauchy: 'Cauchy', sellmeier: 'Sellmeier' };

function materialSelectValue(material) {
  for (const [name, preset] of Object.entries(PRESETS)) {
    if (preset.type !== material.type) continue;
    if (TYPE_FIELDS[preset.type].every((f) => preset[f] === material[f])) {
      return 'preset:' + name;
    }
  }
  return 'type:' + material.type;
}

function buildMaterialUI(body, container) {
  container.innerHTML = '';

  const select = document.createElement('select');
  for (const [key, label] of Object.entries(CUSTOM_TYPES)) {
    select.add(new Option(label + ' (custom)', 'type:' + key));
  }
  for (const name of Object.keys(PRESETS)) {
    select.add(new Option(name, 'preset:' + name));
  }
  select.value = materialSelectValue(body.material);
  select.addEventListener('change', () => {
    const [kind, value] = select.value.split(':');
    if (kind === 'preset') {
      body.material = { ...PRESETS[value] };
    } else if (value !== body.material.type) {
      if (value === 'constant') body.material = { type: 'constant', n: 1.5 };
      else if (value === 'cauchy') body.material = { type: 'cauchy', A: 1.5, B: 0.005, C: 0 };
      else body.material = { ...PRESETS['N-BK7'] };
    }
    buildMaterialUI(body, container);
    requestTrace();
  });
  container.appendChild(select);

  const grid = document.createElement('div');
  grid.className = 'coef-grid';
  for (const field of TYPE_FIELDS[body.material.type]) {
    const label = document.createElement('label');
    label.textContent = field;
    const input = document.createElement('input');
    input.type = 'number';
    input.step = 'any';
    input.value = body.material[field];
    input.addEventListener('change', () => {
      body.material[field] = Number(input.value) || 0;
      if (body.material.type === 'constant') {
        body.material.n = Math.max(1, body.material.n);
        input.value = body.material.n;
      }
      select.value = materialSelectValue(body.material); // drops back to "custom" if edited
      requestTrace();
    });
    label.appendChild(input);
    grid.appendChild(label);
  }
  container.appendChild(grid);

  const hint = document.createElement('div');
  hint.className = 'n-hint';
  hint.dataset.bodyId = String(body.id);
  container.appendChild(hint);
}

function rebuildBodyList() {
  const list = $('body-list');
  list.innerHTML = '';
  if (!faceTable || faceTable.bodies.length === 0) {
    list.textContent = 'No model loaded yet.';
    list.className = 'muted small';
    return;
  }
  list.className = '';
  for (const body of faceTable.bodies) {
    const item = document.createElement('div');
    item.className = 'body-item';

    const name = document.createElement('div');
    name.className = 'body-name';
    name.textContent = `Body ${body.id + 1}`;
    name.title = `${body.faceIds.length} B-rep surfaces`;

    const row = document.createElement('div');
    row.className = 'body-row';
    const label = document.createElement('label');
    label.textContent = `Material (${body.faceIds.length} surfaces)`;
    row.append(label);

    const matBox = document.createElement('div');
    buildMaterialUI(body, matBox);

    item.append(name, row, matBox);
    list.appendChild(item);
  }
  updateIorHints();
}

// ------------------------------------------------------------ model loading

function clearModel() {
  clearSelection();
  clearRays();
  for (const mesh of faceMeshes.values()) {
    mesh.geometry.dispose();
    modelGroup.remove(mesh);
  }
  faceMeshes.clear();
  if (faceTable) disposeFaceTable(faceTable);
  if (shape) shape.delete();
  faceTable = null;
  shape = null;
}

function showOverlay(text) {
  let overlay = $('loading-overlay');
  if (!overlay) {
    overlay = document.createElement('div');
    overlay.id = 'loading-overlay';
    overlay.innerHTML = '<div class="msg"></div><div class="progress-track"><div class="progress-fill"></div></div>';
    viewport.appendChild(overlay);
  }
  overlay.querySelector('.msg').textContent = text;
  return overlay;
}
function hideOverlay() {
  $('loading-overlay')?.remove();
}

async function loadModelFromUrl(url) {
  const overlay = showOverlay('Starting…');
  $('model-status').textContent = 'Loading model…';
  try {
    if (!oc) {
      oc = await initOcct((msg) => { overlay.querySelector('.msg').textContent = msg; });
    }
    overlay.querySelector('.msg').textContent = 'Reading STEP file…';
    clearModel();
    shape = await readStepFromUrl(oc, url);

    overlay.querySelector('.msg').textContent = 'Building B-rep face table…';
    faceTable = buildFaceTable(oc, shape);
    for (const body of faceTable.bodies) body.material = defaultMaterial();

    overlay.querySelector('.msg').textContent = 'Building display mesh…';
    const meshData = buildDisplayMeshes(oc, shape, faceTable, 0.1, 15);
    for (const f of faceTable.faces) {
      const data = meshData.get(f.id);
      if (!data) continue;
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.BufferAttribute(data.positions, 3));
      geometry.setIndex(new THREE.BufferAttribute(data.indices, 1));
      geometry.computeVertexNormals();
      const mesh = new THREE.Mesh(geometry, NORMAL_MATERIAL);
      mesh.userData.faceId = f.id;
      mesh.userData.bodyId = f.bodyId;
      faceMeshes.set(f.id, mesh);
      modelGroup.add(mesh);
    }

    rebuildBodyList();
    fitView();
    $('model-status').textContent =
      `Loaded ${faceTable.bodies.length} body/bodies, ${faceTable.faces.length} surfaces.`;
    requestTrace();
  } catch (err) {
    console.error(err);
    overlay.innerHTML = `<div class="error">${(err && err.message) || err}</div>`;
    $('model-status').textContent = 'Failed to load model — see console.';
    return;
  }
  hideOverlay();
}

// ------------------------------------------------------------ face selection

function clearSelection() {
  if (selectedFaceId !== null) {
    const mesh = faceMeshes.get(selectedFaceId);
    if (mesh) mesh.material = NORMAL_MATERIAL;
  }
  selectedFaceId = null;
  $('face-panel').hidden = false;
  $('face-controls').hidden = true;
}

function selectFace(faceId) {
  const face = faceTable.faces.find((f) => f.id === faceId);
  const mesh = faceMeshes.get(faceId);
  if (!face || !mesh) return;
  clearSelection();
  selectedFaceId = faceId;
  mesh.material = HIGHLIGHT_MATERIAL;

  const idx = faceTable.faces.indexOf(face);
  $('face-info').textContent = `Surface ${idx + 1} of ${faceTable.faces.length} (body ${face.bodyId + 1})`;
  $('face-fresnel').checked = face.reflectivity === null;
  $('face-refl').value = face.reflectivity === null ? 0.5 : face.reflectivity;
  $('face-refl-val').textContent = Number($('face-refl').value).toFixed(2);
  $('face-refl-row').style.opacity = face.reflectivity === null ? 0.4 : 1;
  $('face-panel').hidden = true;
  $('face-controls').hidden = false;
}

// click-vs-drag detection for picking
let downPos = null;
renderer.domElement.addEventListener('pointerdown', (e) => {
  downPos = { x: e.clientX, y: e.clientY };
});
renderer.domElement.addEventListener('pointerup', (e) => {
  if (!downPos) return;
  const moved = Math.hypot(e.clientX - downPos.x, e.clientY - downPos.y);
  downPos = null;
  if (moved > 5) return;
  if (gizmo.dragging || gizmo.axis) return;       // interacting with the light gizmo
  if (aimGizmo.dragging || aimGizmo.axis) return; // interacting with the aim gizmo

  const rect = renderer.domElement.getBoundingClientRect();
  const ndc = new THREE.Vector2(
    ((e.clientX - rect.left) / rect.width) * 2 - 1,
    -((e.clientY - rect.top) / rect.height) * 2 + 1
  );
  const raycaster = new THREE.Raycaster();
  raycaster.firstHitOnly = true;
  raycaster.setFromCamera(ndc, camera);
  const hits = raycaster.intersectObjects([...faceMeshes.values()], false);
  if (hits.length > 0) {
    selectFace(hits[0].object.userData.faceId);
  } else {
    clearSelection();
  }
});

$('face-fresnel').addEventListener('change', () => {
  if (selectedFaceId === null) return;
  const face = faceTable.faces.find((f) => f.id === selectedFaceId);
  if ($('face-fresnel').checked) {
    face.reflectivity = null;
    $('face-refl-row').style.opacity = 0.4;
  } else {
    face.reflectivity = Number($('face-refl').value);
    $('face-refl-row').style.opacity = 1;
  }
  requestTrace();
});
$('face-refl').addEventListener('input', () => {
  $('face-refl-val').textContent = Number($('face-refl').value).toFixed(2);
  if (selectedFaceId === null || $('face-fresnel').checked) return;
  const face = faceTable.faces.find((f) => f.id === selectedFaceId);
  face.reflectivity = Number($('face-refl').value);
  requestTrace();
});
$('btn-deselect').addEventListener('click', clearSelection);

// ----------------------------------------------------------------- tracing

function clearRays() {
  if (rayLines) {
    scene.remove(rayLines);
    rayLines.geometry.dispose();
    rayLines.material.dispose();
    rayLines = null;
  }
}

function trace() {
  if (!oc || !faceTable) return;
  clearRays();
  const p = currentParams();
  updateSceneScale();

  const origin = lightGroup.position.clone();
  let axis = p.emissionMode === 'custom'
    ? aimTarget.position.clone().sub(origin)
    : modelCenter().sub(origin);
  if (axis.lengthSq() < 1e-9) axis.set(-1, 0, 0);
  // the same ray fan is traced once per wavelength, so dispersion shows up
  // as spectral rays sharing a path until refraction separates them
  const directions = emissionDirections(p.rayCount, p.emissionMode, axis, p.coneAngle);
  const wavelengths = traceWavelengths(p);

  const batches = [];
  let totalSegments = 0;
  let totalMs = 0;
  let maxDepth = 0;
  let capped = false;
  for (const wl of wavelengths) {
    const iors = new Map(faceTable.bodies.map((b) => [b.id, iorAt(b.material, wl)]));
    const getIor = (bodyId) => iors.get(bodyId) ?? 1.5;
    const result = traceRaysBrep(oc, faceTable, getIor, {
      origin,
      directions,
      ambientIor: p.ambientIor,
      maxBounces: p.maxBounces,
      minIntensity: p.minIntensity,
      maxDist: sceneDiag * 1.5,
      eps: Math.max(sceneDiag * 1e-5, 1e-5),
    });
    batches.push({ segments: result.segments, rgb: wavelengthToRGB(wl) });
    totalSegments += result.stats.segments;
    totalMs += result.stats.timeMs;
    maxDepth = Math.max(maxDepth, result.stats.maxDepthReached);
    capped = capped || result.stats.capped;
  }

  // intensity is the source's total power output: it is split evenly across
  // all emitted rays and spectral samples (see StepRayOptics for the rationale).
  const REFERENCE_RAYS = 500;
  const gain = (p.intensity * REFERENCE_RAYS) / (p.rayCount * wavelengths.length);
  rayLines = buildRayLines(batches, gain);
  scene.add(rayLines);

  const rgb = wavelengthToRGB(centerWavelength(p));
  glow.color.setRGB(rgb[0], rgb[1], rgb[2]);
  glow.intensity = p.intensity * sceneDiag * 0.5;
  lightMarker.material.color.setRGB(
    0.5 + rgb[0] * 0.5, 0.5 + rgb[1] * 0.5, 0.5 + rgb[2] * 0.5
  );

  $('trace-stats').textContent =
    `${p.rayCount}${wavelengths.length > 1 ? ` × ${wavelengths.length} λ` : ''} rays → ` +
    `${totalSegments.toLocaleString()} segments, depth ≤ ${maxDepth}, ${totalMs.toFixed(0)} ms` +
    (capped ? ' (segment cap hit — lower ray count or bounces)' : '');
}

let traceTimer = null;
function requestTrace() {
  refreshValueLabels();
  if (!$('auto-trace').checked) return;
  clearTimeout(traceTimer);
  traceTimer = setTimeout(trace, 120);
}

// -------------------------------------------------------------- UI wiring

for (const id of ['light-x', 'light-y', 'light-z']) {
  $(id).addEventListener('change', () => {
    lightGroup.position.set(
      Number($('light-x').value), Number($('light-y').value), Number($('light-z').value)
    );
    requestTrace();
  });
}
$('light-gizmo').addEventListener('change', updateWidgetVisibility);
$('show-widgets').addEventListener('change', updateWidgetVisibility);

for (const id of ['aim-x', 'aim-y', 'aim-z']) {
  $(id).addEventListener('change', () => {
    aimTarget.position.set(
      Number($('aim-x').value), Number($('aim-y').value), Number($('aim-z').value)
    );
    requestTrace();
  });
}
$('aim-gizmo').addEventListener('change', updateWidgetVisibility);

for (const id of ['wavelength', 'ray-count', 'cone-angle', 'max-bounces', 'min-intensity', 'spec-samples']) {
  $(id).addEventListener('input', requestTrace);
}
$('intensity').addEventListener('input', () => {
  $('intensity-num').value = Number($('intensity').value).toFixed(2);
  requestTrace();
});
$('intensity-num').addEventListener('change', () => {
  const v = Math.max(0, Number($('intensity-num').value) || 0);
  $('intensity-num').value = v;
  $('intensity').value = v; // clamps itself to the slider range
  requestTrace();
});
for (const id of ['emission-mode', 'light-mode', 'spec-min', 'spec-max', 'ambient-ior']) {
  $(id).addEventListener('change', requestTrace);
}
$('btn-trace').addEventListener('click', () => { refreshValueLabels(); trace(); });
$('btn-clear-rays').addEventListener('click', clearRays);
$('btn-fit').addEventListener('click', fitView);
$('show-grid').addEventListener('change', () => {
  grid.visible = axes.visible = $('show-grid').checked;
});
$('show-models').addEventListener('change', () => {
  modelGroup.visible = $('show-models').checked;
});

window.addEventListener('resize', () => {
  camera.aspect = viewport.clientWidth / viewport.clientHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(viewport.clientWidth, viewport.clientHeight);
});

// -------------------------------------------------------- host integration

// The add-in posts {type:"loadStep", url:"https://app.local/model.step?..."} once the
// WebView2 panel is initialized (see addin/InventorRayOptics/OpticsDockable.cs).
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', (e) => {
    if (e.data && e.data.type === 'loadStep' && e.data.url) {
      loadModelFromUrl(e.data.url);
    }
  });
}

// ------------------------------------------------------------------- start

syncLightInputs();
syncAimInputs();
refreshValueLabels();

// console/testing hook (mirrors StepRayOptics' window.__sro)
window.__iro = {
  scene, camera, get oc() { return oc; }, get faceTable() { return faceTable; },
  selectFace, trace, lightGroup, aimTarget, loadModelFromUrl,
};

(function animate() {
  requestAnimationFrame(animate);
  orbit.update();
  renderer.render(scene, camera);
})();
