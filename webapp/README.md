# webapp/ — web app hosted by WebView2

The HTML/JS app that OpenCascade-loads the exported STEP and ray-traces on its B-rep surfaces.

Most of it is **ported from the sibling `StepRayOptics` project** — see
[`../docs/DEVELOPMENT_SPEC.md`](../docs/DEVELOPMENT_SPEC.md) §2 for the file-by-file reuse map and
§7 Phases 1–3 for the OpenCascade loader (`occt.js`), the B-rep tracer (`brepTracer.js`), the
ported optics (`optics.js`) and materials (`materials.js`), and the UI (`app.js`).

Vendor `three.js` and `opencascade.js` into `vendor/`.
