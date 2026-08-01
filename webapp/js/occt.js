// OpenCascade (WASM) loading, STEP file reading, and B-rep face-table construction.
//
// All ray/geometry intersection and every surface normal used by the optics core come
// from the analytic B-rep data built here (surface handle + trim classifier + location).
// The three.js meshes built here are for DISPLAY ONLY and are never consulted by the
// tracer — see brepTracer.js.
//
// opencascade.js (donalffons, 2.0.0-beta line) ships a minimal base module plus five
// dynamic-library "profile" bundles that must be side-loaded in dependency order:
//   core -> modelingAlgorithms -> visualApplication -> dataExchangeBase -> dataExchangeExtra
// STEPControl_Reader lives in dataExchangeExtra; GeomAPI_IntCS/GeomLProp_SLProps/
// BRepTopAdaptor_FClass2d/BRepBndLib live in modelingAlgorithms. All five are required —
// dataExchangeExtra fails to link (undefined TDF_Attribute symbols) without
// visualApplication loaded first, even though we never call into it directly.
//
// Every OCCT object created here (points, directions, handles, explorers, ...) must be
// `.delete()`d once unused -- WASM heap objects are not garbage collected. Long-lived
// objects (the analytic surface handle, the classifier, the location) are kept for the
// lifetime of the face table entry and are the caller's responsibility to dispose via
// disposeFaceTable().

const VENDOR_DIR = '../vendor/opencascade/';

const LIB_ORDER = [
  'core',
  'modelingAlgorithms',
  'visualApplication',
  'dataExchangeBase',
  'dataExchangeExtra',
];

let occtPromise = null;

async function fetchBuffer(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`Failed to fetch ${url}: ${res.status}`);
  return new Uint8Array(await res.arrayBuffer());
}

async function initOcctInternal(onProgress) {
  const initOpenCascade = (await import(VENDOR_DIR + 'opencascade.js')).default;

  onProgress?.('Downloading OpenCascade core…');
  const wasmBinary = await fetchBuffer(VENDOR_DIR + 'opencascade.wasm');
  const oc = await new initOpenCascade({ wasmBinary });

  const libBuffers = {};
  for (const lib of LIB_ORDER) {
    onProgress?.(`Downloading OpenCascade module: ${lib}…`);
    libBuffers[lib + '.wasm'] = await fetchBuffer(`${VENDOR_DIR}opencascade.${lib}.wasm`);
  }
  const fsShim = { readFile: (libFile) => libBuffers[libFile] };

  for (const lib of LIB_ORDER) {
    onProgress?.(`Linking OpenCascade module: ${lib}…`);
    await oc.loadDynamicLibrary(lib + '.wasm', {
      loadAsync: true, global: true, nodelete: true, allowUndefined: false, fs: fsShim,
    });
  }

  return oc;
}

/** Loads and links OpenCascade exactly once; subsequent calls reuse the same instance. */
export function initOcct(onProgress) {
  if (!occtPromise) occtPromise = initOcctInternal(onProgress);
  return occtPromise;
}

// ---------------------------------------------------------------- STEP loading

/**
 * @returns TopoDS_Shape (caller owns; delete() when the model is discarded)
 */
export async function readStepFromUrl(oc, url) {
  const bytes = await fetchBuffer(url);
  const virtualPath = '/model_' + Math.floor(Math.random() * 1e9) + '.step';
  oc.FS.writeFile(virtualPath, bytes);

  const reader = new oc.STEPControl_Reader_1();
  const status = reader.ReadFile(virtualPath);
  oc.FS.unlink(virtualPath);
  if (status !== oc.IFSelect_ReturnStatus.IFSelect_RetDone) {
    reader.delete();
    throw new Error('Failed to parse STEP file (ReadFile did not return RetDone)');
  }

  const progress = new oc.Message_ProgressRange_1();
  reader.TransferRoots(progress);
  const shape = reader.OneShape();
  progress.delete();
  reader.delete();

  if (shape.IsNull()) throw new Error('STEP file produced an empty shape');
  return shape;
}

// ---------------------------------------------------------------- face table

/**
 * One entry per B-rep face of the shape. `surface`, `classifier`, and `location` are
 * long-lived analytic OCCT handles used by the tracer for every ray/surface test against
 * this face; they are NOT derived from any triangulation.
 *
 * @typedef {Object} BrepFace
 * @property {number} id
 * @property {TopoDS_Face} face
 * @property {Handle_Geom_Surface} surface   - untransformed (face-local) analytic surface
 * @property {TopLoc_Location} location      - transform from face-local to body space
 * @property {boolean} reversed              - true if the face's natural normal must flip
 * @property {BRepTopAdaptor_FClass2d} classifier - 2D (u,v) trim test for this face
 * @property {{min:[number,number,number], max:[number,number,number]}} bbox
 * @property {number|null} reflectivity      - null = Fresnel; otherwise 0..1
 * @property {number} bodyId
 */

function buildBbox(oc, face) {
  const bbox = new oc.Bnd_Box_1();
  oc.BRepBndLib.Add(face, bbox, false);
  const lo = bbox.CornerMin();
  const hi = bbox.CornerMax();
  const result = {
    min: [lo.X(), lo.Y(), lo.Z()],
    max: [hi.X(), hi.Y(), hi.Z()],
  };
  bbox.delete();
  lo.delete();
  hi.delete();
  return result;
}

/**
 * Explodes a shape into its solids (bodies) and, for each, its B-rep faces.
 * @returns {{ faces: BrepFace[], bodies: {id:number, faceIds:number[]}[] }}
 */
export function buildFaceTable(oc, shape) {
  const faces = [];
  const bodies = [];
  let nextFaceId = 0;

  const solidExplorer = new oc.TopExp_Explorer_1();
  let bodyId = 0;
  let sawAnySolid = false;
  for (
    solidExplorer.Init(shape, oc.TopAbs_ShapeEnum.TopAbs_SOLID, oc.TopAbs_ShapeEnum.TopAbs_SHAPE);
    solidExplorer.More();
    solidExplorer.Next(), bodyId++
  ) {
    sawAnySolid = true;
    const solid = solidExplorer.Current();
    const faceIds = collectFacesOfShape(oc, solid, bodyId, faces, () => nextFaceId++);
    bodies.push({ id: bodyId, faceIds });
  }
  solidExplorer.delete();

  // fall back to treating the whole shape as one body if it has no TopAbs_SOLID
  // (e.g. a bare shell export)
  if (!sawAnySolid) {
    const faceIds = collectFacesOfShape(oc, shape, 0, faces, () => nextFaceId++);
    bodies.push({ id: 0, faceIds });
  }

  // aggregate per-body bbox lets the tracer skip an entire body's faces with one
  // cheap test when a ray can't possibly reach any of them (see brepTracer.js)
  for (const body of bodies) {
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (const id of body.faceIds) {
      const f = faces[id];
      for (let i = 0; i < 3; i++) {
        min[i] = Math.min(min[i], f.bbox.min[i]);
        max[i] = Math.max(max[i], f.bbox.max[i]);
      }
    }
    body.bbox = { min, max };
  }

  return { faces, bodies };
}

function collectFacesOfShape(oc, shape, bodyId, outFaces, nextId) {
  const ids = [];
  const explorer = new oc.TopExp_Explorer_1();
  for (
    explorer.Init(shape, oc.TopAbs_ShapeEnum.TopAbs_FACE, oc.TopAbs_ShapeEnum.TopAbs_SHAPE);
    explorer.More();
    explorer.Next()
  ) {
    const face = oc.TopoDS.Face_1(explorer.Current());
    const location = new oc.TopLoc_Location_1();
    const surface = oc.BRep_Tool.Surface_1(face, location);
    if (surface.IsNull()) { location.delete(); continue; } // skip degenerate faces

    const reversed = face.Orientation_1() === oc.TopAbs_Orientation.TopAbs_REVERSED;
    const classifier = new oc.BRepTopAdaptor_FClass2d(face, 1e-7);
    const bbox = buildBbox(oc, face);

    // Precompute local<->world transforms once per face (not per ray): BRep_Tool.Surface
    // returns the surface in the face's own local frame, so every ray/surface test must
    // move the ray into local space and move the resulting point/normal back to world.
    const trsfFwd = location.Transformation();
    const trsfInv = trsfFwd.Inverted();

    const id = nextId();
    outFaces.push({
      id, face, surface, location, reversed, classifier, bbox, trsfFwd, trsfInv,
      reflectivity: null, bodyId,
    });
    ids.push(id);
  }
  explorer.delete();
  return ids;
}

export function disposeFaceTable(faceTable) {
  for (const f of faceTable.faces) {
    f.face.delete();
    f.surface.delete();
    f.location.delete();
    f.classifier.delete();
    f.trsfFwd.delete();
    f.trsfInv.delete();
  }
}

// ---------------------------------------------------------------- display mesh

/**
 * Builds one three.js BufferGeometry per face for on-screen display. This is a
 * tessellation for rendering only; the tracer never reads it.
 * @returns {Map<number, {positions:Float32Array, normals:Float32Array, indices:Uint32Array}>}
 */
export function buildDisplayMeshes(oc, shape, faceTable, linearDeflection, angularDeflectionDeg) {
  const mesh = new oc.BRepMesh_IncrementalMesh_2(
    shape, linearDeflection, false, angularDeflectionDeg * Math.PI / 180, false);
  const progress = new oc.Message_ProgressRange_1();
  mesh.Perform_1(progress);
  progress.delete();

  const result = new Map();
  for (const f of faceTable.faces) {
    const triLoc = new oc.TopLoc_Location_1();
    const triHandle = oc.BRep_Tool.Triangulation(f.face, triLoc);
    if (triHandle.IsNull()) { triHandle.delete(); triLoc.delete(); continue; }
    const tri = triHandle.get(); // raw ref into triHandle — do not use after it is deleted

    const trsf = triLoc.Transformation();
    const nbNodes = tri.NbNodes();
    const nbTris = tri.NbTriangles();
    const positions = new Float32Array(nbNodes * 3);
    for (let i = 1; i <= nbNodes; i++) {
      const localP = tri.Node(i);
      const p = localP.Transformed(trsf);
      localP.delete();
      positions[(i - 1) * 3 + 0] = p.X();
      positions[(i - 1) * 3 + 1] = p.Y();
      positions[(i - 1) * 3 + 2] = p.Z();
      p.delete();
    }

    const indices = new Uint32Array(nbTris * 3);
    const a = { current: 0 }, b = { current: 0 }, c = { current: 0 };
    for (let i = 1; i <= nbTris; i++) {
      const t = tri.Triangle(i);
      t.Get(a, b, c);
      t.delete();
      const base = (i - 1) * 3;
      if (f.reversed) {
        indices[base + 0] = a.current - 1;
        indices[base + 1] = c.current - 1;
        indices[base + 2] = b.current - 1;
      } else {
        indices[base + 0] = a.current - 1;
        indices[base + 1] = b.current - 1;
        indices[base + 2] = c.current - 1;
      }
    }

    result.set(f.id, { positions, indices });
    trsf.delete();
    triHandle.delete(); // frees the shared Poly_Triangulation JS-side reference
    triLoc.delete();
  }
  mesh.delete();
  return result;
}
