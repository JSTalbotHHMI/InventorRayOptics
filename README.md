# InventorRayOptics

An Autodesk Inventor add-in that adds an **optical ray-tracing environment** for the currently
open part (`.ipt`) or assembly (`.iam`). Click a ribbon button and a docked panel opens showing a
point light source tracing rays that refract and reflect through the model in 3D — with control
over index of refraction (incl. wavelength dispersion), per-surface reflectivity, wavelength,
intensity, and ray density.

It is the Inventor-native sibling of [StepRayOptics](../StepRayOptics) (the standalone browser
version). This project is **Inventor-only**.

## How it works

1. The C# add-in exports the active document to a temporary **STEP** file (true B-rep geometry).
2. It opens a dockable panel hosting a **WebView2** browser control.
3. The web app reads the STEP with **OpenCascade (WASM)** and ray-traces light against the
   **analytic B-rep surfaces** — not a triangulated mesh — rendering with three.js.

> **Core guarantee:** all intersection and all surface normals used in the physics come from the
> exact analytic surfaces. Triangles are used only to draw the model on screen and for
> bounding-box broad-phase culling — never in the optical result.

## Status

🚧 **Not yet implemented.** This repository currently contains only the development specification.
Implementation is being handed to a separate coding model.

**➡ The complete build instructions are in [`docs/DEVELOPMENT_SPEC.md`](docs/DEVELOPMENT_SPEC.md).**
Start there. It defines the architecture, the phased plan, verified API reference snippets
(Inventor STEP export, WebView2 hosting, OpenCascade B-rep tracing), and acceptance tests.

## Prerequisites (for the implementer)

- Visual Studio 2022, Windows
- Autodesk Inventor 2023+ with the **Inventor SDK** installed (for `Autodesk.Inventor.Interop`)
- .NET Framework 4.8 (the add-in target)
- WebView2 Runtime (preinstalled on current Windows)
- `opencascade.js` and `three.js` vendored into `webapp/vendor/`

## Repository layout

```
addin/     C# Inventor add-in (Visual Studio solution)
webapp/    Web app hosted by WebView2 (opencascade.js + three.js)
docs/      DEVELOPMENT_SPEC.md — the build instructions
```

## License / attribution

Internal project. Physics and UI are ported from the sibling StepRayOptics project.
