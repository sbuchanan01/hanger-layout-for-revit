# Hanger Layout for Revit — Claude Code orientation

This is a standalone Revit add-in (builds for Revit 2025 and Revit 2026
from one source tree via `-p:RevitVersion=...`). It places pipe and duct
hangers along selected Autodesk Fabrication parts using user-defined
size-banded spacing rules.

## Project layout

```
src/
├── HangerLayout.csproj            ← .NET 8 + Revit refs (RevitVersion-parameterized, defaults 2026), AfterBuild deploy target
├── HangerLayout.addin             ← Revit add-in manifest (drops into Addins\<version>\ matching build)
├── HangerLayoutApp.cs             ← IExternalApplication: ribbon tab + ExternalEvent registration
├── HangerLayoutCommand.cs         ← IExternalCommand: opens the dialog
├── Models/
│   └── HangerSpecModels.cs        ← SupportSpec, SupportSpecRow, enums (DuctShape, JointMode, etc.)
├── Revit/
│   ├── HangerPlacer.cs            ← Core placement algorithm (chain-spanning, joint-gap accounting)
│   ├── HangerFlowMap.cs           ← BFS flow map for chain orientation
│   ├── HangerSpecStore.cs         ← ExtensibleStorage save/load on doc.ProjectInformation
│   ├── HangerSettingsStore.cs     ← Per-project Fab Database folder hint
│   ├── HangerSelectionFilters.cs  ← ISelectionFilter for PickObject
│   ├── HangerWarningSwallower.cs  ← IFailuresPreprocessor (silences expected warnings)
│   ├── HSpecsMapReader.cs         ← Parses Fab's HSpecs.MAP binary format
│   ├── SupportMapDumper.cs        ← Generic MAP file utility / debug dumper
│   ├── PartTypeClassifier.cs      ← PCF-type + IsStraightPipe + StraightDuctCids
│   ├── ConnectorHelper.cs         ← GetPhysicalConnectors
│   ├── MapFileHelper.cs           ← zlib MAP envelope decoder
│   ├── RevitEventHandler.cs       ← Generic IExternalEventHandler for modeless dialogs
│   └── RibbonIconFactory.cs       ← Ring-hanger icon (DrawingVisual → ImageSource)
└── UI/
    ├── HangerLayoutDialog.xaml    ← WPF modeless dialog
    └── HangerLayoutDialog.xaml.cs ← MVVM-ish view-model + event handlers

docs/                              ← User-facing + developer documentation
```

## Build and deploy

```
cd src
dotnet build -c Debug                       # default = Revit 2026
dotnet build -c Debug -p:RevitVersion=2025  # Revit 2025
```

Debug builds auto-deploy `HangerLayout.dll` + `HangerLayout.addin` to
`%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\`. Output goes into
`bin/Debug-Revit<RevitVersion>/` so the two builds don't overwrite
each other. A `REVIT<version>` compile-time symbol (e.g. `REVIT2026`)
is also defined for any source that needs to branch on the API.

If Revit is open, the DLL is locked — close Revit and rebuild, or skip
the deploy step and copy manually.

Override the Revit install path at the command line if your install
isn't the default:

```
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

## Critical Revit API gotchas

These have bitten the original author multiple times — keep them in mind
when modifying the placement logic.

- **`ConnectorManager.Connectors` enumeration is unstable across calls.**
  Snapshot to a list before iterating multiple times.
- **`Connector` wrappers are never `ReferenceEquals` between calls** — even
  for the "same" physical connector. Compare by Origin (with tolerance) or
  by `ConnectorManager.Owner.Id` + index.
- **`XYZ.IsAlmostEqualTo` is NOT plain Euclidean distance.** It's a
  component-wise tolerance. Use `a.DistanceTo(b) < tol` if you mean
  "within X feet".
- **`PickObject(ObjectType.PointOnElement, filter)`** — the filter's
  `AllowReference` must return `true` to accept reference picks. Easy to
  forget when subclassing.
- **PCF is pipe-domain only.** PartTypeClassifier still has SKEY methods
  inherited from the parent project — they're harmless dead code here but
  not exercised.

## Key design decisions worth knowing

- **Hanger placement is chain-aware, not segment-aware.** If a 30 ft run
  consists of two 14 ft straights with a flange between them, hangers space
  uniformly across the chain, treating the flange as a 3-inch gap rather
  than two independent placements per straight.
- **Spacing math is hanger-to-hanger, not fitting-to-hanger.** The first
  hanger goes at `leftBound + spacing` (where `leftBound` accounts for
  fitting/joint setback). Subsequent hangers step `spacing` apart until
  `pos >= rightBound`.
- **Hanger-button shape filter has three-step precedence** for mixed
  round/rect duct runs:
  1. Explicit ROUND/PIPE keyword in the button name → accept for round
     hosts only.
  2. RECTANGULAR / BEARER / TRAPEZE keyword → accept for rect hosts only.
  3. No shape keyword → fall back to "round default" (Revit's convention).
- **Specs storage is ExtensibleStorage on `doc.ProjectInformation`**, not
  shared parameters. Survives save/close, travels with the model. Schema
  GUID is `8C3F2B4E-9D4F-4C9B-B67E-3D5F92DA014F` (independent of any other
  add-in).

## When you change the placement algorithm

Test cases that matter:
- Single straight, no joints, span > 2× spacing.
- Two-straight chain joined by a flange — verify joint piece length is
  in the spacing, not added on.
- Mixed round/rect duct run with a Round→Rect reducer in the middle.
- Selection starts on a joint piece (the seed) — the chain walker should
  find the adjacent straights.
- Pick a Start Node on the left vs right end of a chain — placement should
  begin from that end's setback.

## When you change the UI

The dialog uses a hand-rolled INPC pattern (no MVVM framework). Look at
`HangerLayoutViewModel` for the data binding contract. Dirty tracking is
explicit (`IsSpecsDirty` flag) — set it whenever a SupportSpec is mutated
so the close-prompt fires.
