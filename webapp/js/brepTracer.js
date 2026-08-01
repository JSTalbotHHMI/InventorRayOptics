// Ray tracer against the analytic B-rep face table built by occt.js.
//
// Every hit point and every surface normal here comes from OpenCascade's exact analytic
// surface evaluation (GeomAPI_IntCS for intersection, GeomLProp_SLProps for the normal) —
// never from a triangle. A pure-JS ray/bounding-box test is used only as a broad-phase
// filter to decide which faces are even worth an exact OCCT test; it never affects the
// optical result, only which faces get skipped before the expensive analytic call.
//
// OCCT WASM objects are not garbage collected. Every temporary created inside the hot
// per-ray-per-face loop is deleted before moving on; only the face table's long-lived
// handles (surface, classifier, trsfFwd/trsfInv) survive across calls.

import * as THREE from 'three';
import { computeBounce } from './optics.js';

const MAX_SEGMENTS = 60000; // B-rep intersection is far slower than a triangle BVH —
                             // keep the per-trace budget modest (see DEVELOPMENT_SPEC §7).

// Ray vs. axis-aligned bounding box (slab method), padded by `pad` on every side.
function rayHitsBox(ox, oy, oz, dx, dy, dz, box, pad) {
  let tmin = -Infinity, tmax = Infinity;
  const o = [ox, oy, oz], d = [dx, dy, dz];
  for (let i = 0; i < 3; i++) {
    const lo = box.min[i] - pad, hi = box.max[i] + pad;
    if (Math.abs(d[i]) < 1e-14) {
      if (o[i] < lo || o[i] > hi) return false;
      continue;
    }
    let t1 = (lo - o[i]) / d[i];
    let t2 = (hi - o[i]) / d[i];
    if (t1 > t2) { const tmp = t1; t1 = t2; t2 = tmp; }
    tmin = Math.max(tmin, t1);
    tmax = Math.min(tmax, t2);
    if (tmin > tmax) return false;
  }
  return tmax >= 0;
}

/**
 * Exact analytic intersection of a world-space ray against one B-rep face.
 * @returns {{ dist:number, point:THREE.Vector3, normal:THREE.Vector3 } | null}
 */
function intersectFace(oc, f, origin, dir, eps) {
  // move the ray into the face's local (untransformed-surface) frame
  const worldPnt = new oc.gp_Pnt_3(origin.x, origin.y, origin.z);
  const worldDir = new oc.gp_Dir_4(dir.x, dir.y, dir.z);
  const localPnt = worldPnt.Transformed(f.trsfInv);
  const localDir = worldDir.Transformed(f.trsfInv);
  worldPnt.delete(); worldDir.delete();

  const line = new oc.Geom_Line_3(localPnt, localDir);
  localPnt.delete(); localDir.delete();
  const lineHandle = new oc.Handle_Geom_Curve_2(line);
  // NOTE: Handle_Geom_Curve takes ownership of `line` (OCCT Handle<T> is a refcounted
  // smart pointer) — once wrapped, only `lineHandle.delete()` may be called. Calling
  // `line.delete()` too is a double-free: it doesn't crash immediately (the first few
  // hundred calls "work"), it corrupts the WASM dynamic-linking function table and
  // crashes later with an opaque "null function or function signature mismatch" /
  // "table index out of bounds" — do not reintroduce it.

  const intCS = new oc.GeomAPI_IntCS_2(lineHandle, f.surface);

  let best = null;
  if (intCS.IsDone()) {
    const n = intCS.NbPoints();
    const uOut = { current: 0 }, vOut = { current: 0 }, wOut = { current: 0 };
    for (let i = 1; i <= n; i++) {
      intCS.Parameters_1(i, uOut, vOut, wOut);
      const dist = wOut.current;
      if (dist <= eps) continue; // behind the ray origin, or the surface we just left
      if (best && dist >= best.dist) continue;

      const uv = new oc.gp_Pnt2d_3(uOut.current, vOut.current);
      const state = f.classifier.Perform(uv, 1e-7);
      uv.delete();
      const onFace = state === oc.TopAbs_State.TopAbs_IN || state === oc.TopAbs_State.TopAbs_ON;
      if (!onFace) continue;

      const props = new oc.GeomLProp_SLProps_1(f.surface, uOut.current, vOut.current, 1, 1e-7);
      if (!props.IsNormalDefined()) { props.delete(); continue; }
      const localNormal = props.Normal();
      props.delete();
      const worldNormal = localNormal.Transformed(f.trsfFwd);
      localNormal.delete();

      const localPoint = intCS.Point(i);
      const worldPoint = localPoint.Transformed(f.trsfFwd);
      localPoint.delete();

      best = {
        dist,
        point: new THREE.Vector3(worldPoint.X(), worldPoint.Y(), worldPoint.Z()),
        normal: new THREE.Vector3(worldNormal.X(), worldNormal.Y(), worldNormal.Z()).normalize(),
      };
      worldPoint.delete();
      worldNormal.delete();
    }
  }

  intCS.delete();
  lineHandle.delete(); // also frees the underlying `line` — see note above
  return best;
}

/**
 * @param faceTable  from occt.buildFaceTable()
 * @param getIor     (bodyId) => index of refraction at the traced wavelength
 * @param params     { origin, directions, maxBounces, minIntensity, maxDist, eps, ambientIor }
 * @returns { segments: [{a,b,energy}], stats }
 */
export function traceRaysBrep(oc, faceTable, getIor, params) {
  const t0 = performance.now();
  const { origin, directions, maxBounces, minIntensity, maxDist, eps } = params;
  const ambientIor = params.ambientIor ?? 1.0;
  const pad = Math.max(eps * 10, 1e-6);

  const segments = [];
  let capped = false;
  let maxDepthReached = 0;

  const stack = [];
  for (const dir of directions) {
    stack.push({ origin: origin.clone(), dir: dir.clone(), energy: 1, depth: 0 });
  }

  while (stack.length > 0) {
    if (segments.length >= MAX_SEGMENTS) { capped = true; break; }
    const ray = stack.pop();
    maxDepthReached = Math.max(maxDepthReached, ray.depth);

    const start = ray.origin.clone().addScaledVector(ray.dir, eps);

    let nearest = null;
    let nearestFace = null;
    for (const body of faceTable.bodies) {
      // one cheap test skips this body's whole face list when the ray can't reach it —
      // matters for assemblies where parts are spread out (see occt.buildFaceTable)
      if (!rayHitsBox(start.x, start.y, start.z, ray.dir.x, ray.dir.y, ray.dir.z, body.bbox, pad)) continue;
      for (const faceId of body.faceIds) {
        const f = faceTable.faces[faceId];
        if (!rayHitsBox(start.x, start.y, start.z, ray.dir.x, ray.dir.y, ray.dir.z, f.bbox, pad)) continue;
        const hit = intersectFace(oc, f, start, ray.dir, eps);
        if (hit && (!nearest || hit.dist < nearest.dist)) {
          nearest = hit;
          nearestFace = f;
        }
      }
    }

    if (!nearest) {
      const end = start.clone().addScaledVector(ray.dir, maxDist);
      segments.push({ a: ray.origin, b: end, energy: ray.energy });
      continue;
    }

    segments.push({ a: ray.origin, b: nearest.point, energy: ray.energy });
    if (ray.depth >= maxBounces) continue;

    // orient the normal against the incoming ray; pick media accordingly
    const bodyIor = getIor(nearestFace.bodyId);
    let n1 = ambientIor, n2 = bodyIor;
    const n = nearest.normal.clone();
    if (n.dot(ray.dir) > 0) {
      // hit from the inside: leaving the material
      n.negate();
      n1 = bodyIor; n2 = ambientIor;
    }

    const { reflectDir, refractDir, R, tir } = computeBounce(
      ray.dir, n, n1, n2, nearestFace.reflectivity);

    const reflectedEnergy = ray.energy * R;
    const refractedEnergy = ray.energy * (1 - R);

    if (reflectedEnergy >= minIntensity) {
      stack.push({ origin: nearest.point.clone(), dir: reflectDir, energy: reflectedEnergy, depth: ray.depth + 1 });
    }
    if (!tir && refractedEnergy >= minIntensity) {
      stack.push({ origin: nearest.point.clone(), dir: refractDir, energy: refractedEnergy, depth: ray.depth + 1 });
    }
  }

  return {
    segments,
    stats: {
      raysEmitted: directions.length,
      segments: segments.length,
      maxDepthReached,
      capped,
      timeMs: performance.now() - t0,
    },
  };
}
