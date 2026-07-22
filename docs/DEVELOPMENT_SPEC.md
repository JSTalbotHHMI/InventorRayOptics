# InventorRayOptics — Development Specification

**Audience:** the coding model implementing this project (qwen2.5-coder or similar).
**Status:** greenfield. No code exists yet except this spec and a scaffolded folder layout.
**Golden rule:** follow the phases in order. Each phase ends with an **acceptance test** you
must be able to pass before moving on. Do not start a later phase until the earlier one runs.

---

## 1. What you are building

A **C# add-in for Autodesk Inventor** that adds an optical ray-tracing environment for the
currently open part (`.ipt`) or assembly (`.iam`). When the user clicks the ribbon button:

1. The add-in **exports the active document to a temporary STEP file** (true B-rep geometry).
2. It opens a **dockable panel inside Inventor** that hosts a **WebView2** browser control.
3. The WebView2 loads a local **web app** that uses **OpenCascade (WASM)** to read the STEP
   file, then **ray-traces light through the true B-rep surfaces** and renders the result with
   three.js: a point light source, refraction (Snell), reflection (Fresnel / per-surface), total
   internal reflection, wavelength/dispersion, intensity, ray count, and view controls.

### Hard requirement — this is the whole point of the project
**All ray/geometry intersection and all surface normals MUST be computed from the analytic
B-rep surfaces (planes, cylinders, spheres, cones, tori, NURBS), NOT from a triangulated mesh.**
A triangle mesh is allowed **only** for (a) drawing the shaded model on screen and (b) computing
per-face bounding boxes as a broad-phase filter. The optical result — every hit point and every
refraction/reflection normal — must come from OpenCascade's analytic surface evaluation. If you
ever find yourself using a triangle's face normal in the physics, you have done it wrong.

### Explicit non-goals (do NOT build these)
- No standalone browser mode. This project only runs inside Inventor. (The sibling project
  `StepRayOptics` is the browser version; leave it alone.)
- No two-way selection or live auto-refresh in v1 (they are optional Phase 4 stretch goals).
- No assemblies-specific features beyond "it also works on `.iam`".

---

## 2. Reuse from the sibling project `StepRayOptics`

A working browser ray-tracer already exists at `../StepRayOptics` (same GitHub owner). **Port
from it aggressively.** Its physics and UI are correct and tested; only its *geometry +
intersection* layer must be replaced (it uses a triangle BVH; you will use OpenCascade B-rep).

| StepRayOptics file | What to do with it |
|---|---|
| `js/tracer.js` | **Split it.** The optics math (Snell refraction, Fresnel `fresnelR`, TIR, energy bookkeeping, `emissionDirections`, `wavelengthToRGB`, `buildRayLines`) is reusable **verbatim** → put in `optics.js`. The intersection loop (`raycaster.intersectObjects`) is **replaced** by the OpenCascade tracer → `brepTracer.js`. |
| `js/materials.js` | **Port verbatim.** Constant / Cauchy / Sellmeier IOR models + glass presets. Unchanged. |
| `js/loader.js` | **Replace.** Was occt-import-js→mesh. New loader reads STEP with full OpenCascade and builds the B-rep face table (§7 Phase 1). |
| `js/app.js` | **Port most of it.** three.js scene, camera, `OrbitControls`, `TransformControls` gizmos, the whole sidebar UI wiring, point-light gizmo, per-face selection/highlight, per-body material UI, transform gizmos, widget toggle. Swap the trace call to the new B-rep tracer. |
| `index.html`, `style.css` | Port and adjust. |
| `vendor/three.module.js`, `OrbitControls.js`, `TransformControls.js` | Reuse the same versions. |

Read those files first. Do not re-derive the optics; copy it.

---

## 3. Architecture & data flow

```
┌─────────────────────────── Autodesk Inventor (process) ───────────────────────────┐
│                                                                                    │
│  C# Add-in (InventorRayOptics.dll, .NET Framework 4.8, COM-visible)                │
│   • ApplicationAddInServer  → adds ribbon button on Part & Assembly ribbons        │
│   • On click:                                                                       │
│       1. Export active doc → %TEMP%\InventorRayOptics\model.step  (STEP translator) │
│       2. Copy STEP into the mapped web folder as  model.step                        │
│       3. Create/show DockableWindow, AddChild(HWND of a WinForms host control)      │
│       4. WinForms host contains a WebView2                                          │
│       5. PostWebMessageAsJson({type:"loadStep", url:"https://app.local/model.step"})│
│                                                                                    │
│   ┌──────────────────────── DockableWindow ────────────────────────┐              │
│   │  WinForms UserControl (Dock=Fill)                               │              │
│   │   └── WebView2  →  https://app.local/index.html  (web app)      │              │
│   └────────────────────────────────────────────────────────────────┘              │
└────────────────────────────────────────────────────────────────────────────────────┘
                                    │  chrome.webview messages  │
                                    ▼                           ▲
        Web app (HTML/JS, hosted from the add-in's /webapp folder via
        SetVirtualHostNameToFolderMapping "app.local")
          • opencascade.js (WASM)  → read STEP, iterate faces, analytic surfaces
          • brepTracer.js          → ray↔surface intersection + exact normals (B-REP)
          • optics.js              → Snell / Fresnel / TIR / dispersion (ported)
          • materials.js           → Cauchy / Sellmeier IOR (ported)
          • three.js               → display shells (tessellated) + ray polylines
          • app.js                 → scene, gizmos, sidebar UI (ported)
```

STEP is passed to the web app as a **URL it fetches**, not as a giant string message — write the
`.step` file into the web folder and post its `app.local` URL.

---

## 4. Tech stack & prerequisites

- **Visual Studio 2022** (Community is fine), Windows.
- **Autodesk Inventor** installed (2023+). The developer must have the **Inventor SDK** installed
  (`C:\Users\Public\Documents\Autodesk\Inventor <ver>\SDK\`) to get `Autodesk.Inventor.Interop.dll`.
- **.NET Framework 4.8** — target this for the add-in. Inventor add-ins load into Inventor's
  process; .NET Framework 4.8 is the safe, supported target. Do **not** use .NET 5/6/7/8 for the
  add-in DLL.
- **WebView2**: NuGet package `Microsoft.Web.WebView2`. The **WebView2 Runtime** must be present on
  the machine (it is preinstalled on current Windows 10/11).
- **Web app**: plain HTML/JS ES modules (no build step required). Libraries vendored locally:
  - `three` (copy the exact modules used by StepRayOptics).
  - **opencascade.js** — the WebAssembly OpenCascade build. Get it from npm
    (`opencascade.js`, package by *donalffons*). Vendor `opencascade.full.js` +
    `opencascade.full.wasm` locally (the WASM is tens of MB — that is expected; commit it or
    document a fetch step). Load via its documented `initOpenCascade()` initializer.

**Generate one GUID** for the add-in and reuse it as both ClassId and ClientId. A pre-generated
one you may use: `8210C5FB-411B-4F93-9034-58FEFBFA35BC` (or run `[guid]::NewGuid()` in PowerShell
for a fresh one). Use the **same** GUID in the C# `[Guid(...)]` attribute and the `.addin` manifest.

---

## 5. Repository layout to create

```
InventorRayOptics/
├── README.md                         (already present)
├── docs/
│   └── DEVELOPMENT_SPEC.md           (this file)
├── addin/                            (C# Visual Studio solution)
│   ├── InventorRayOptics.sln
│   └── InventorRayOptics/
│       ├── InventorRayOptics.csproj
│       ├── StandardAddInServer.cs    (ApplicationAddInServer)
│       ├── OpticsCommand.cs          (button handler: export + show panel)
│       ├── StepExporter.cs           (STEP export via translator)
│       ├── OpticsDockable.cs         (DockableWindow + WinForms host + WebView2)
│       ├── OpticsHostControl.cs      (WinForms UserControl containing WebView2)
│       ├── InventorRayOptics.addin   (manifest, copied to Inventor Addins folder)
│       └── Properties/AssemblyInfo.cs
├── webapp/                           (served by WebView2)
│   ├── index.html
│   ├── style.css
│   ├── js/
│   │   ├── app.js                    (scene + UI, ported)
│   │   ├── optics.js                 (Snell/Fresnel/TIR/dispersion, ported)
│   │   ├── materials.js              (Cauchy/Sellmeier, ported verbatim)
│   │   ├── brepTracer.js             (NEW: OpenCascade B-rep intersection + trace loop)
│   │   └── occt.js                   (NEW: OpenCascade init + STEP load + face table)
│   └── vendor/
│       ├── three.module.js
│       ├── OrbitControls.js
│       ├── TransformControls.js
│       ├── opencascade.full.js
│       └── opencascade.full.wasm
└── .gitignore                        (ignore bin/, obj/, *.user)
```

---

## 6. Cross-cutting rules

- **Units.** Inventor's internal unit is centimetres, but STEP export writes the document's
  modeling units (usually **mm**). OpenCascade reads STEP as-is, so the web app works in **mm**.
  Keep all light coordinates, distances, and epsilons in mm. State the unit in the UI.
- **OpenCascade memory.** opencascade.js objects live on the WASM heap and are **not** garbage
  collected. You **must call `.delete()`** on every OCCT object you `new` once you are done with it
  (readers, explorers, points, directions, curves, props, classifiers, intersectors). Leaking will
  crash the tab after a few traces. Build small helpers that create→use→delete. This is the #1
  source of bugs — take it seriously.
- **opencascade.js binding names carry numeric suffixes** for C++ overloads
  (`STEPControl_Reader_1`, `GeomAPI_IntCS_2`, `Geom_Line_3`, `GeomLProp_SLProps_1`, …). The exact
  suffix depends on the build. **Consult the generated TypeScript definitions** (`.d.ts`) shipped
  with the opencascade.js build to confirm each constructor/overload before using it. Do not guess.
- **Ray counts.** B-rep intersection is far slower than triangle BVH. Design for **hundreds to a
  few thousand** rays, not 20,000. Add a per-face bounding-box broad phase (see Phase 2).
- **Threading.** All Inventor API calls run on Inventor's main STA thread (the add-in's thread).
  WebView2 async init (`EnsureCoreWebView2Async`) is awaited on that same UI thread. Do not spin up
  background threads for Inventor calls.

---

## 7. Implementation phases

### Phase 0 — Add-in shell showing a WebView2 (no geometry yet)

**Goal:** Inventor loads the add-in; a ribbon button opens a dockable panel showing a static local
web page.

Tasks:
1. Create the C# Class Library project targeting **.NET Framework 4.8**. Reference
   `Autodesk.Inventor.Interop` (set *Embed Interop Types = False*, *Copy Local = False*) and add the
   `Microsoft.Web.WebView2` NuGet package and `System.Windows.Forms`.
2. Implement `StandardAddInServer : Inventor.ApplicationAddInServer` (skeleton below). In
   `Activate`, create a `ButtonDefinition` and add it to a custom ribbon panel on both the **Part**
   and **Assembly** ribbons (Tools tab is fine).
3. Write the `.addin` manifest (below) and a post-build step (or manual instructions) that copies
   it + the DLL to `%ProgramData%\Autodesk\Inventor <ver>\Addins\`.
4. On button click, create a `DockableWindow`, host a WinForms `OpticsHostControl` (a `UserControl`
   with a `WebView2` docked Fill) via `AddChild(control.Handle)`, and navigate the WebView2 to a
   static `index.html` served from the `webapp` folder via `SetVirtualHostNameToFolderMapping`.

**Reference — `StandardAddInServer.cs`:**
```csharp
using System.Runtime.InteropServices;
using Inventor;

namespace InventorRayOptics
{
    [Guid("8210C5FB-411B-4F93-9034-58FEFBFA35BC"), ComVisible(true)]
    public class StandardAddInServer : ApplicationAddInServer
    {
        private Inventor.Application _inv;
        private ButtonDefinition _launchBtn;
        private OpticsDockable _dockable;
        internal static string AddInClientId = "{8210C5FB-411B-4F93-9034-58FEFBFA35BC}";

        public void Activate(ApplicationAddInSite site, bool firstTime)
        {
            _inv = site.Application;

            var defs = _inv.CommandManager.ControlDefinitions;
            _launchBtn = defs.AddButtonDefinition(
                "Ray Optics", "IROptics:Launch",
                CommandTypesEnum.kNonShapeEditCmdType, AddInClientId,
                "Open the optical ray-tracing panel for the active model",
                "Trace light rays through the active part/assembly");
            _launchBtn.OnExecute += OnLaunch;

            AddButtonToRibbon("Part");
            AddButtonToRibbon("Assembly");
        }

        private void AddButtonToRibbon(string ribbonName)
        {
            var ribbon = _inv.UserInterfaceManager.Ribbons[ribbonName];
            var tab = ribbon.RibbonTabs["id_TabTools"];
            RibbonPanel panel;
            try { panel = tab.RibbonPanels["IROptics:Panel"]; }
            catch { panel = tab.RibbonPanels.Add("Ray Optics", "IROptics:Panel", AddInClientId); }
            panel.CommandControls.AddButton(_launchBtn, true);
        }

        private void OnLaunch(NameValueMap context)
        {
            var doc = _inv.ActiveDocument;
            if (doc == null) return;
            _dockable ??= new OpticsDockable(_inv);
            _dockable.ShowFor(doc);   // Phase 0: just show panel; Phase 1: export+load
        }

        public void Deactivate()
        {
            _dockable?.Dispose();
            if (_launchBtn != null) _launchBtn.OnExecute -= OnLaunch;
            Marshal.ReleaseComObject(_inv);
            _inv = null;
        }

        public void ExecuteCommand(int commandID) { }
        public object Automation => null;
    }
}
```

**Reference — `InventorRayOptics.addin`** (place in the Inventor `Addins` folder; set
`<Assembly>` to the built DLL path, or a relative path if co-located):
```xml
<?xml version="1.0" encoding="utf-8"?>
<Addin Type="Standard">
  <ClassId>{8210C5FB-411B-4F93-9034-58FEFBFA35BC}</ClassId>
  <ClientId>{8210C5FB-411B-4F93-9034-58FEFBFA35BC}</ClientId>
  <DisplayName>Inventor Ray Optics</DisplayName>
  <Description>Optical ray-tracing environment for the active model</Description>
  <Assembly>InventorRayOptics.dll</Assembly>
  <LoadOnStartUp>1</LoadOnStartUp>
  <UserUnloadable>1</UserUnloadable>
  <Hidden>0</Hidden>
  <SupportedSoftwareVersionGreaterThan>22..</SupportedSoftwareVersionGreaterThan>
</Addin>
```

**Reference — `OpticsHostControl.cs` (WinForms host with WebView2):**
```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace InventorRayOptics
{
    public class OpticsHostControl : UserControl
    {
        public WebView2 Web { get; } = new WebView2 { Dock = DockStyle.Fill };
        public event EventHandler<string> MessageFromWeb;

        public OpticsHostControl() { Controls.Add(Web); }

        public async Task InitAsync(string webRootFolder)
        {
            await Web.EnsureCoreWebView2Async(null);
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local", webRootFolder, CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;   // F12 for web-side debugging
            Web.CoreWebView2.WebMessageReceived += (s, e) =>
                MessageFromWeb?.Invoke(this, e.TryGetWebMessageAsString());
            Web.CoreWebView2.Navigate("https://app.local/index.html");
        }

        public void PostJson(string json) =>
            Web.CoreWebView2.PostWebMessageAsJson(json);
    }
}
```

**Reference — `OpticsDockable.cs` (create the panel, host the control):**
```csharp
using System;
using System.IO;
using Inventor;

namespace InventorRayOptics
{
    public class OpticsDockable : IDisposable
    {
        private readonly Inventor.Application _inv;
        private DockableWindow _dw;
        private OpticsHostControl _host;
        private string WebRoot => Path.Combine(
            Path.GetDirectoryName(typeof(OpticsDockable).Assembly.Location), "webapp");

        public OpticsDockable(Inventor.Application inv) { _inv = inv; }

        public async void ShowFor(Document doc)
        {
            if (_host == null)
            {
                _host = new OpticsHostControl();
                _host.CreateControl();                 // force HWND creation
                _host.MessageFromWeb += (s, msg) => { /* Phase 4: handle selection */ };

                var uiMgr = _inv.UserInterfaceManager;
                _dw = uiMgr.DockableWindows.Add(
                    StandardAddInServer.AddInClientId, "IROptics:Dock", "Ray Optics");
                _dw.AddChild(_host.Handle);
                _dw.ShowVisibility = true;
                _dw.DockingState = DockingStateEnum.kDockRight;
                _dw.Visible = true;

                await _host.InitAsync(WebRoot);        // wait for WebView2 before posting
            }
            _dw.Visible = true;
            // Phase 1 adds: export STEP for `doc`, copy into WebRoot, post loadStep message.
        }

        public void Dispose()
        {
            _host?.Dispose();
            if (_dw != null) { _dw.Visible = false; }
        }
    }
}
```

**Phase 0 acceptance test:**
- Build the DLL, deploy the `.addin` + DLL, restart Inventor.
- Open any part. A **Ray Optics** button appears in the Tools tab of the Part ribbon.
- Clicking it docks a panel on the right showing a static `index.html` (put "Hello from web app"
  in it for now). No errors in Inventor and no errors in the WebView2 devtools console (F12).

---

### Phase 1 — STEP export + load & display the model via OpenCascade

**Goal:** clicking the button exports the active model to STEP, the web app loads it with
OpenCascade, builds the face table, and displays the shaded model (tessellated for display only)
in three.js.

**C# side — `StepExporter.cs`** (verified translator GUID + pattern):
```csharp
using System.IO;
using Inventor;

namespace InventorRayOptics
{
    public static class StepExporter
    {
        // Inventor STEP translator add-in GUID (documented, stable).
        private const string StepTranslatorId = "{90AF7F40-0C01-11D5-8E83-0010B541CD80}";

        public static string ExportActive(Inventor.Application inv, Document doc)
        {
            var outDir = Path.Combine(Path.GetTempPath(), "InventorRayOptics");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, "model.step");

            var translator = (TranslatorAddIn)inv.ApplicationAddIns.ItemById[StepTranslatorId];
            var ctx = inv.TransientObjects.CreateTranslationContext();
            ctx.Type = IOMechanismEnum.kFileBrowseIOMechanism;

            var opts = inv.TransientObjects.CreateNameValueMap();
            if (translator.HasSaveCopyAsOptions[doc, ctx, opts])
            {
                // ApplicationProtocolType: 3 = AP214. Prefer AP242 if the enum value is
                // available in your Inventor version (enumerate options per the ADN blog).
                opts.Value["ApplicationProtocolType"] = 3;
            }

            var data = inv.TransientObjects.CreateDataMedium();
            data.FileName = outPath;
            translator.SaveCopyAs(doc, ctx, opts, data);
            return outPath;
        }
    }
}
```
Then in `OpticsDockable.ShowFor`, after the panel is up: call `StepExporter.ExportActive`, copy the
resulting `model.step` into `WebRoot` (so it is reachable as `https://app.local/model.step`), and
`_host.PostJson("{\"type\":\"loadStep\",\"url\":\"https://app.local/model.step\"}")`.

**Web side — `occt.js`** (init + read STEP + build the face table). The **face table** is the
central data structure the tracer uses:
```
faceTable = [
  {
    id: <int>,               // index; also the three.js group name for picking
    surface: <Handle_Geom_Surface>,   // analytic surface — KEEP for tracing (do not delete)
    face: <TopoDS_Face>,     // KEEP; used for trim classification
    classifier: <BRepTopAdaptor_FClass2d>,  // KEEP; UV inside/outside test
    bbox: {min:[x,y,z], max:[x,y,z]},       // from BRepBndLib — broad phase
    bodyId: <int>,           // which solid this face belongs to
    reflectivity: null,      // null = use Fresnel; else 0..1 (set from UI)
  }, ...
]
bodies = [ { id, material /* materials.js object */, faceIds:[...] }, ... ]
```

Load sketch (confirm exact binding names against the `.d.ts`):
```js
import initOpenCascade from '../vendor/opencascade.full.js';
let oc = null;
export async function initOcct() { oc = await initOpenCascade(); return oc; }

export async function loadStepUrl(url) {
  const buf = new Uint8Array(await (await fetch(url)).arrayBuffer());
  oc.FS.writeFile('/model.step', buf);
  const reader = new oc.STEPControl_Reader_1();
  const status = reader.ReadFile('/model.step');
  if (status !== oc.IFSelect_ReturnStatus.IFSelect_RetDone) throw new Error('STEP read failed');
  reader.TransferRoots(new oc.Message_ProgressRange_1());
  const shape = reader.OneShape();   // TopoDS_Shape (keep)
  oc.FS.unlink('/model.step');
  return shape;
}
```

Build the face table (per face): get located analytic surface, bbox, classifier, and a display
triangulation. Confirm each call in the `.d.ts`; expected shapes:
- `oc.BRep_Tool.Surface_2(face)` → `Handle_Geom_Surface` (located to world). If your build only
  exposes the unlocated overload, apply the face `TopLoc_Location` to the geometry yourself.
- `face.Orientation_1()` → `TopAbs_Orientation` (`TopAbs_REVERSED` ⇒ flip normal).
- Bounding box: `const b = new oc.Bnd_Box_1(); oc.BRepBndLib.Add_2(face, b, false); b.CornerMin()/CornerMax()`.
- Trim classifier: `new oc.BRepTopAdaptor_FClass2d(face, 1e-6)`; later `.Perform(new oc.gp_Pnt2d_2(u,v))`.
- Display mesh: `new oc.BRepMesh_IncrementalMesh_2(face, deflection, false, angDefl, false)`, then
  `const tri = oc.BRep_Tool.Triangulation(face, new oc.TopLoc_Location_1(), 0)` → read
  nodes/triangles into a three.js `BufferGeometry` (one geometry per face → one mesh in a group per
  body). Apply the face location transform to node coordinates. **Node/Triangle accessor names
  changed across OCCT versions — verify in the `.d.ts`.**

Render the per-face meshes with a translucent `MeshStandardMaterial` (copy the look from
StepRayOptics `makeObjectMaterial`). Keep `userData.faceId` / `userData.bodyId` for picking.

**Phase 1 acceptance test:**
- Click the button on a real `.ipt`. A `model.step` appears in `%TEMP%\InventorRayOptics\`.
- The web panel shows the shaded model, correctly oriented and scaled (mm).
- `faceTable.length` equals the number of B-rep faces (log it). Curved faces are one entry each.
- No WASM memory error; devtools console clean.

---

### Phase 2 — B-rep ray tracer + optics core

**Goal:** trace a point-source light through the B-rep and draw the rays. Physics identical to
StepRayOptics but intersection is analytic.

1. **Port `optics.js`** from StepRayOptics `tracer.js`: `wavelengthToRGB`, `emissionDirections`,
   `fresnelR`, `buildRayLines`, and the reflect/refract vector math. These are pure functions —
   copy them.
2. **Port `materials.js` verbatim** (`iorAt`, presets, `TYPE_FIELDS`).
3. **Write `brepTracer.js`.** Core loop mirrors StepRayOptics `traceRays`, but the "find nearest
   hit" step uses OpenCascade. Algorithm per ray segment:

   ```
   nearestHit = null
   for each face in faceTable:
       if ray misses face.bbox (slab test) → continue          # broad phase (bbox only)
       line   = Geom_Line through (origin, dir)                  # oc.Geom_Line_3(gp_Pnt, gp_Dir)
       intCS  = new oc.GeomAPI_IntCS_2(new oc.Handle_Geom_Curve_2(line), face.surface)
       n      = intCS.NbPoints()
       for i in 1..n:
           P  = intCS.Point(i)                                   # gp_Pnt (world)
           # parameters: W on curve, (U,V) on surface
           let U={}, V={}, W={};  intCS.Parameters(i, U, V, W)   # check .d.ts signature
           if W.current <= EPS → continue                        # must be forward of origin
           st = face.classifier.Perform(new oc.gp_Pnt2d_2(U,V))  # trim test
           if st !== TopAbs_IN && st !== TopAbs_ON → continue    # outside the trimmed face
           dist = W.current
           if dist < nearestHit.dist → nearestHit = {P, U, V, dist, face}
           <delete P>
       <delete intCS, line, handles>
   ```

   Then compute the **exact normal** at the winning hit:
   ```
   props = new oc.GeomLProp_SLProps_1(nearestHit.face.surface,
                                      nearestHit.U, nearestHit.V, 1, 1e-7)
   if props.IsNormalDefined():
       N = props.Normal()             # gp_Dir, analytic
   if face.Orientation == TopAbs_REVERSED: N = -N   # outward from solid
   <delete props>
   ```

   Hand `hitPoint`, `N`, the face's `reflectivity`, and the body's `iorAt(material, λ)` to the
   ported optics (Snell + Fresnel/manual + TIR), spawn reflected & refracted rays, and recurse
   (max bounces, min energy) exactly as StepRayOptics does. Emit the same additive-blended
   `LineSegments`. Support single wavelength and spectrum (trace per λ) as StepRayOptics does.

4. **Wrap OCCT allocations** in create→use→`delete()` helpers. Do not leak `gp_Pnt`, `gp_Dir`,
   `gp_Pnt2d`, `Geom_Line`, handles, `GeomAPI_IntCS`, `GeomLProp_SLProps`.

**Slab bbox test** (broad phase, pure JS, no OCCT): standard ray-AABB. This is the only place
triang*-ish* acceleration appears, and it uses **bounding boxes from `BRepBndLib`**, not display
triangles, and it never affects the optical result — it only skips faces the ray cannot reach.

**Phase 2 acceptance test (physics correctness):**
- Model a simple **plano/round lens or a ball** in Inventor, IOR 1.5, trace a small cone: rays
  converge behind it (focusing). The bend pattern is **smooth**, not faceted — proving analytic
  normals. Compare qualitatively to StepRayOptics' triangulated ball: this one has no facet kinks.
- A **flat window** (parallel faces) laterally offsets a tilted beam without changing its
  direction (classic slab behavior).
- Total internal reflection appears at shallow exit angles.
- Repeated traces do not grow WASM memory unbounded (watch `performance.memory` / task manager).

---

### Phase 3 — Full UI port

Port the StepRayOptics sidebar and interactions into `app.js` / `index.html` / `style.css`:
- Point light source: XYZ inputs + `TransformControls` gizmo; wavelength; **intensity as a text box
  = total source power split across rays** (copy the power-conserving gain formula); ray count;
  emission mode (full sphere / cone at model / cone custom-aim with aim gizmo); cone angle.
- Tracing: surrounding-medium IOR (`ambientIor`), max bounces, min intensity, auto-retrace toggle.
- **Per-surface reflectivity:** click a face in the view → resolve `userData.faceId` → edit
  `faceTable[id].reflectivity` (null = Fresnel, else 0..1). Highlight the picked face (reuse the
  StepRayOptics highlight approach, but per-face three.js mesh makes it trivial: highlight that
  mesh).
- **Per-body material:** list bodies; each gets a material selector (constant / Cauchy / Sellmeier
  + glass presets) driving `iorAt(material, λ)`.
- Model transforms: Move/Rotate/Scale gizmos + Reset (optional — the model comes from Inventor at a
  fixed pose; still useful for arranging light vs model). If kept, apply the transform to both the
  display mesh and the OCCT geometry used for tracing (transform the ray into face-local space, or
  transform the surface — simplest: transform the ray origin/dir by the inverse of the body
  transform before intersection, then transform results back).
- View: orbit/pan/zoom, Fit, grid toggle, model visibility, **Show control widgets** master toggle.

**Phase 3 acceptance test:** every control changes the trace as in StepRayOptics; picking a curved
STEP surface highlights exactly that one B-rep face (no scattered triangles).

---

### Phase 4 — Optional stretch goals (only if Phases 0–3 are solid)

- **Refresh from model** button: re-export STEP, reload, keep camera/light settings.
- **Two-way selection:** clicking a face in the viewer highlights the matching Inventor `Face`
  (`Document`/`HighlightSet`), and vice-versa. **Hard part:** mapping OCCT (STEP) face order to
  Inventor `Face` objects — STEP export does not preserve Inventor entity IDs. Approach: at export
  time, also walk `PartComponentDefinition.SurfaceBodies[].Faces` in C# and build a geometric
  fingerprint per face (area + centroid + surface type); do the same in JS from OCCT
  (`GProp_GProps` / surface type) and match by nearest fingerprint. Document it as best-effort.
- **True "environment"** (enter/exit like Stress Analysis) via the Inventor `Environments` API
  instead of a plain ribbon button. Cosmetic; do last.

---

## 8. Gotchas checklist (read before you start each phase)

- [ ] Add-in targets **.NET Framework 4.8**, is **ComVisible**, and the **GUID matches** the `.addin`.
- [ ] `Autodesk.Inventor.Interop` reference has **Embed Interop Types = False**.
- [ ] `.addin` file is in the correct **`%ProgramData%\Autodesk\Inventor <ver>\Addins\`** folder and
      `<Assembly>` points at the real DLL. Restart Inventor after changes.
- [ ] `OpticsHostControl.CreateControl()` is called before `AddChild(Handle)` (need a valid HWND).
- [ ] `await EnsureCoreWebView2Async` **before** any `CoreWebView2` call or `PostWebMessageAsJson`.
- [ ] STEP is passed as a **fetchable URL** in the mapped folder, not as a huge string message.
- [ ] Every OCCT `new` has a matching **`.delete()`** (except the long-lived face-table handles).
- [ ] opencascade.js constructor/overload **suffixes verified against the `.d.ts`** — do not guess.
- [ ] Physics uses the **analytic normal** (`GeomLProp_SLProps.Normal`), never a triangle normal.
- [ ] Work in **mm**; keep epsilons scaled to model size.
- [ ] Keep ray counts modest; bbox broad phase is on.

---

## 9. Final acceptance (whole project)

1. Open a lens/prism `.ipt` in Inventor → click **Ray Optics** → docked panel shows the shaded
   model within a couple of seconds.
2. A point source emits rays that refract/reflect through the model with **smooth, analytic**
   behavior (curved surfaces focus without faceting).
3. Wavelength/dispersion, intensity (power/ray), ray count, surrounding medium, per-body IOR
   (Cauchy/Sellmeier), and per-face reflectivity all work.
4. Clicking a curved surface selects exactly that B-rep face.
5. Runs repeatedly without WASM memory blowups.
6. `StepRayOptics` is untouched.

---

## 10. Verified references

- Inventor `SurfaceBody.FindUsingRay` (B-rep ray, returns entities **and** hit points) —
  https://help.autodesk.com/view/INVNTOR/2025/ENU/?guid=SurfaceBody_FindUsingRay and forum:
  https://forums.autodesk.com/t5/inventor-customization/inventor-api-how-to-locate-an-edge-and-or-face/td-p/6545559
  (Not required for the STEP+OCCT path chosen here, but the canonical native-B-rep ray API if ever
  wanted.)
- Inventor STEP export via translator add-in (GUID `{90AF7F40-0C01-11D5-8E83-0010B541CD80}`,
  `SaveCopyAs` + `NameValueMap` + `DataMedium`) —
  https://help.autodesk.com/cloudhelp/2022/ENU/Inventor-API/files/TranslatorAddIn5_Sample.htm and
  https://adndevblog.typepad.com/manufacturing/2014/02/get-option-names-and-values-supported-by-inventor-translator-addins-via-api.html
- Inventor exact face normal via `Face.Evaluator` / `SurfaceEvaluator.GetNormal` —
  https://adndevblog.typepad.com/manufacturing/2012/08/what-is-the-best-way-to-compute-a-normal-of-a-face-in-inventor-api.html
- opencascade.js (WASM OpenCascade) — https://github.com/donalffons/opencascade.js ; STEP read +
  face iteration pattern: https://github.com/donalffons/opencascade.js/issues/156
- OpenCascade `GeomAPI_IntCS` (curve/surface intersection) —
  https://dev.opencascade.org/doc/refman/html/class_geom_a_p_i___int_c_s.html
- WebView2 in WinForms (`EnsureCoreWebView2Async`, `SetVirtualHostNameToFolderMapping`,
  messaging) — https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winforms
- Inventor `DockableWindow` (`DockableWindows.Add`, `AddChild(HWND)`) —
  http://www.hjalte.nl/tutorials/69-dockable-window
```
