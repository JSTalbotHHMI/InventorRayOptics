# InventorRayOptics

An Autodesk Inventor add-in that adds an **optical ray-tracing environment** for the
currently open part (`.ipt`) or assembly (`.iam`). Click a ribbon button and a docked
panel opens showing a point light source tracing rays that refract and reflect through
the model in 3D — with control over index of refraction (incl. wavelength dispersion),
per-surface reflectivity, wavelength, intensity, and ray density.

It is the Inventor-native sibling of [StepRayOptics](../StepRayOptics) (the standalone
browser version). This project is **Inventor-only**.

## How it works

1. The **Environments** tab gets a "Ray Optics" entry, next to Stress Analysis and
   Inventor Studio. Clicking it switches the document into a dedicated "Ray Optics"
   environment — its own ribbon tab becomes active, while the normal modeling tabs stay
   available (this tool is a passive viewer, not an exclusive editing mode) — and Inventor
   automatically provides the standard Finish/return affordance out of that environment.
2. The C# add-in exports the active document to a temporary **STEP** file (true B-rep geometry).
3. It opens a dockable panel hosting a **WebView2** browser control (unchanged across
   environment switches — it's a docked panel beside the model, not merged into
   Inventor's own 3D view; see "Environment vs. same-window rendering" below).
4. The web app reads the STEP with **OpenCascade (WASM)** and ray-traces light against the
   **analytic B-rep surfaces** — not a triangulated mesh — rendering with three.js.

The Ray Optics tab's "Refresh Model" button re-exports and reloads without leaving the
environment — useful after editing geometry in another tab.

### Environment vs. same-window rendering

Inventor's `Environment` API only swaps ribbon tabs/panels — it does not replace or
merge with the 3D graphics view. Stress Analysis/Inventor Studio get their "results in
the same window" look by drawing directly into Inventor's native 3D view via its
**ClientGraphics** API, not by embedding another control over it. This project
deliberately keeps the simpler architecture: the ray-traced view lives in its own
WebView2/three.js panel beside Inventor's native view, not drawn into it. Merging the
two would mean replacing the three.js rendering of rays with native ClientGraphics calls
from C# — a real rearchitecture, intentionally not done here.

> **Core guarantee:** all intersection and all surface normals used in the physics come
> from the exact analytic surfaces (`GeomAPI_IntCS` for intersection, `GeomLProp_SLProps`
> for normals). Triangles are used only to draw the model on screen and never appear in
> the optical result.

## Status

✅ Implemented and verified live in Autodesk Inventor 2025: ribbon button → Environment
switch → STEP export → WebView2 panel → OpenCascade load → live B-rep ray-trace
rendering, all confirmed via screenshot capture of the running app. Also stress-tested
standalone in a browser against real STEP files (a 7-surface part and an 18-body,
160-surface assembly; 15 consecutive retraces up to ~15k rays, zero crashes).
See [`docs/DEVELOPMENT_SPEC.md`](docs/DEVELOPMENT_SPEC.md) for the full architecture,
phased build plan, and verified API reference this was built from — including a gotchas
section on two hard-won bugs (an OCCT memory-corruption double-free, a WebView2
navigation race) worth reading before touching `brepTracer.js`/`occt.js` again.

## Building

```
addin/InventorRayOptics/InventorRayOptics.csproj    — MSBuild, .NET Framework 4.8, x64
```
Build with MSBuild (no `dotnet` SDK required):
```
MSBuild.exe addin/InventorRayOptics/InventorRayOptics.csproj -p:Configuration=Debug -p:Platform=x64
```
This also copies `webapp/` into `bin/Debug/webapp/` next to the DLL (see the
`CopyWebApp` target in the `.csproj`). Deploy the build output folder (DLL + `.addin`
manifest + `webapp/` + `WebView2Loader.dll`) into
`%APPDATA%\Autodesk\Inventor 2025\Addins\` and restart Inventor.

`webapp/vendor/opencascade/` holds the vendored OpenCascade WASM build (~119 MB across
6 files: the base module plus the `core`/`modelingAlgorithms`/`visualApplication`/
`dataExchangeBase`/`dataExchangeExtra` dynamic-library profiles, all required — see
`js/occt.js` for why). It is committed directly; no separate fetch step is needed.

## Dev-time verification without Inventor

`webapp/` can be exercised standalone in any browser (`python webapp/serve.py 8360`),
since the OpenCascade/three.js pipeline has no Inventor dependency — only the
"export the active document" trigger does. `window.__iro.loadModelFromUrl(url)` in
the browser console drives the same load path the add-in's `postMessage` uses.
`webapp/samples/` holds two STEP files used for this (not part of the shipped add-in).

## Repository layout

```
addin/     C# Inventor add-in (Visual Studio-compatible MSBuild project)
webapp/    Web app hosted by WebView2 (opencascade.js + three.js)
  js/occt.js        OpenCascade init, STEP loading, B-rep face-table + display mesh
  js/brepTracer.js  ray tracer against the analytic B-rep face table
  js/optics.js      Snell/Fresnel/TIR/dispersion math (ported from StepRayOptics)
  js/materials.js   Cauchy/Sellmeier IOR models + glass presets (ported verbatim)
  js/app.js         three.js scene, UI wiring
docs/      DEVELOPMENT_SPEC.md — architecture, phased build plan, verified API notes
tools/     nuget.exe (vendored; no system NuGet/dotnet SDK required to build)
```

## Known limitations

- Per-body/per-model transform gizmos (move/rotate/scale) are not implemented — the
  model is shown at the pose Inventor exported it in. (StepRayOptics has these; they
  were scoped out here since Inventor already supplies a fixed pose.)
- Two-way selection (viewer ↔ Inventor face highlight) is not implemented — STEP export
  doesn't preserve Inventor's internal face IDs, so this would need a geometric
  best-effort matching pass (see DEVELOPMENT_SPEC §7 Phase 4).
- B-rep intersection is brute-force per face (with a bbox broad phase, including a
  per-body aggregate bbox pre-filter for assemblies). It comfortably handles the
  documented "hundreds to a few thousand" rays; a 160-surface, 18-body assembly at
  ~1,000 rays takes a few seconds rather than being instant — expected given exact
  analytic intersection is far more expensive than a triangle BVH.
