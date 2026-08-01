// Pure optics math shared by every geometry backend (ported verbatim from
// StepRayOptics/js/tracer.js — the physics is identical; only how a ray finds its next
// surface differs between the triangle-BVH tracer there and the B-rep tracer here).

import * as THREE from 'three';

const GOLDEN_ANGLE = Math.PI * (3 - Math.sqrt(5));

// Approximate conversion of a visible wavelength (nm) to linear RGB.
export function wavelengthToRGB(wl) {
  let r = 0, g = 0, b = 0;
  if (wl >= 380 && wl < 440) { r = -(wl - 440) / 60; b = 1; }
  else if (wl < 490) { g = (wl - 440) / 50; b = 1; }
  else if (wl < 510) { g = 1; b = -(wl - 510) / 20; }
  else if (wl < 580) { r = (wl - 510) / 70; g = 1; }
  else if (wl < 645) { r = 1; g = -(wl - 645) / 65; }
  else if (wl <= 750) { r = 1; }
  // fade toward the edges of the visible range
  let f = 1;
  if (wl >= 380 && wl < 420) f = 0.3 + 0.7 * (wl - 380) / 40;
  else if (wl > 700 && wl <= 750) f = 0.3 + 0.7 * (750 - wl) / 50;
  const gamma = 0.8;
  return [
    Math.pow(r * f, gamma),
    Math.pow(g * f, gamma),
    Math.pow(b * f, gamma),
  ];
}

// Deterministic, evenly spaced directions (Fibonacci spiral) either over the
// full sphere or over a spherical cap of half-angle `coneAngle` around `axis`.
export function emissionDirections(count, mode, axis, coneAngleDeg) {
  const dirs = [];
  const cosCap = mode === 'sphere'
    ? -1 // full sphere
    : Math.cos(THREE.MathUtils.degToRad(coneAngleDeg));

  // orthonormal basis around the axis
  const w = axis.clone().normalize();
  const u = new THREE.Vector3(1, 0, 0);
  if (Math.abs(w.x) > 0.9) u.set(0, 1, 0);
  u.cross(w).normalize();
  const v = new THREE.Vector3().crossVectors(w, u);

  for (let k = 0; k < count; k++) {
    const cosA = 1 - (1 - cosCap) * ((k + 0.5) / count);
    const sinA = Math.sqrt(Math.max(0, 1 - cosA * cosA));
    const phi = GOLDEN_ANGLE * k;
    const d = new THREE.Vector3()
      .addScaledVector(u, sinA * Math.cos(phi))
      .addScaledVector(v, sinA * Math.sin(phi))
      .addScaledVector(w, cosA);
    dirs.push(d.normalize());
  }
  return dirs;
}

// Exact unpolarized Fresnel reflectance for dielectrics.
export function fresnelR(n1, n2, cosI, cosT) {
  const rs = (n1 * cosI - n2 * cosT) / (n1 * cosI + n2 * cosT);
  const rp = (n1 * cosT - n2 * cosI) / (n1 * cosT + n2 * cosI);
  return 0.5 * (rs * rs + rp * rp);
}

/**
 * Given an incoming ray direction and the OUTWARD surface normal (already oriented
 * against the incoming ray, i.e. dot(dir, normal) < 0), computes the Snell/Fresnel/TIR
 * split for one surface hit. Geometry-agnostic: the caller supplies the normal however
 * it was obtained (analytic B-rep evaluation here; a triangle normal in StepRayOptics).
 *
 * @param dir        THREE.Vector3, incoming ray direction (unit)
 * @param normal     THREE.Vector3, outward normal at the hit, unit, same side as -dir
 * @param n1          index of refraction of the medium the ray is leaving
 * @param n2          index of refraction of the medium the ray is entering
 * @param manualReflectivity  0..1, or null to use the physical Fresnel coefficient
 * @returns {{ reflectDir: THREE.Vector3, refractDir: THREE.Vector3|null, R: number, tir: boolean }}
 */
export function computeBounce(dir, normal, n1, n2, manualReflectivity) {
  const cosI = -dir.dot(normal);
  const eta = n1 / n2;
  const sinT2 = eta * eta * (1 - cosI * cosI);
  const tir = sinT2 > 1;
  const cosT = tir ? 0 : Math.sqrt(1 - sinT2);

  let R;
  if (tir) R = 1;
  else if (manualReflectivity !== null && manualReflectivity !== undefined) R = manualReflectivity;
  else R = fresnelR(n1, n2, cosI, cosT);

  const reflectDir = dir.clone().reflect(normal).normalize();
  const refractDir = tir ? null : dir.clone().multiplyScalar(eta)
    .addScaledVector(normal, eta * cosI - cosT)
    .normalize();

  return { reflectDir, refractDir, R, tir };
}

// Build one LineSegments object from traced segment batches, one batch per
// wavelength: [{ segments, rgb }, ...]. Batches blend additively, so where
// spectral rays overlap (before dispersion separates them) they sum to white.
// `gain` converts a ray's relative energy to screen brightness — the caller
// derives it from source power / ray count so brightness is power-conserving.
export function buildRayLines(batches, gain) {
  let total = 0;
  for (const b of batches) total += b.segments.length;
  const positions = new Float32Array(total * 6);
  const colors = new Float32Array(total * 6);
  let i = 0;
  for (const { segments, rgb } of batches) {
    for (const s of segments) {
      positions[i * 6 + 0] = s.a.x;
      positions[i * 6 + 1] = s.a.y;
      positions[i * 6 + 2] = s.a.z;
      positions[i * 6 + 3] = s.b.x;
      positions[i * 6 + 4] = s.b.y;
      positions[i * 6 + 5] = s.b.z;
      const w = Math.min(1, s.energy * gain);
      for (const off of [0, 3]) {
        colors[i * 6 + off + 0] = rgb[0] * w;
        colors[i * 6 + off + 1] = rgb[1] * w;
        colors[i * 6 + off + 2] = rgb[2] * w;
      }
      i++;
    }
  }
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  const material = new THREE.LineBasicMaterial({
    vertexColors: true,
    transparent: true,
    blending: THREE.AdditiveBlending,
    depthWrite: false,
  });
  return new THREE.LineSegments(geometry, material);
}
