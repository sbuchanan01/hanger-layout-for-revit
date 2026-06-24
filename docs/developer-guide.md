# Developer guide

How to set up your development environment, build, debug, and ship changes.

---

## Prerequisites

- **Windows 10/11** — Revit only runs on Windows.
- **Revit 2025 or 2026** — full install. The add-in references DLLs
  from your Revit install folder (default
  `C:\Program Files\Autodesk\Revit <version>\`). Build defaults to
  Revit 2026; pass `-p:RevitVersion=2025` to target 2025 instead.
- **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download).
  The csproj targets `net8.0-windows`.
- **A C# IDE** (any will do):
  - Visual Studio 2022 / 2026 Community — open `src/HangerLayout.csproj`.
  - JetBrains Rider — open the same file.
  - VS Code + C# Dev Kit — same.
  - Claude Code — open the repo root; the included `CLAUDE.md` orients it.

---

## Clone and build

```powershell
git clone https://github.com/sbuchanan01/hanger-layout-for-revit.git
cd hanger-layout-for-revit/src
dotnet build -c Debug
```

Successful output ends with:

```
Build succeeded.
    0 Error(s)
Time Elapsed 00:00:04.xx
```

(Some `MSB3277` warnings about RevitAPIUI re-references are normal and
harmless.)

Debug builds auto-deploy to `%APPDATA%\Autodesk\Revit\Addins\<version>\`
via the `DeployToRevitAddins` MSBuild target — the `<version>` matches
whatever `-p:RevitVersion=...` you passed (default 2026). **If Revit is
open, the DLL is locked** — the copy step is skipped (the build itself
still succeeds).

If your Revit is installed somewhere non-default:

```powershell
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

To build for **both** Revit 2025 and 2026 in one go (typical for
releases):

```powershell
dotnet build -c Release -p:RevitVersion=2025
dotnet build -c Release -p:RevitVersion=2026
```

Outputs land in `bin/Release-Revit2025/` and `bin/Release-Revit2026/`
respectively.

---

## Debug-and-iterate cycle

The typical inner loop:

1. **Close Revit.** This releases the DLL lock so the post-build copy can
   land.
2. Make code changes in your IDE.
3. `dotnet build -c Debug` (or hit Build in your IDE).
4. **Open Revit**, open a model that contains Fabrication parts.
5. Click the **Hanger Layout** button to launch the dialog. Repro your test
   case.
6. Loop.

If you want to **attach a debugger**, the standard Revit add-in debug
workflow is:

1. Open the csproj in Visual Studio.
2. Open **Properties** on the project → Debug → "Launch Profile" → set the
   executable to your Revit binary
   (`C:\Program Files\Autodesk\Revit <version>\Revit.exe`).
3. Hit F5 — Visual Studio starts Revit with the debugger attached.
4. Set breakpoints in HangerLayout source; they hit when the relevant code
   path runs in Revit.

For Rider, it's the same idea via Run/Debug configurations → .NET
Executable.

---

## Code layout

See [architecture.md](architecture.md) for a full file-by-file tour. The
short version:

- **`src/HangerLayoutApp.cs`** — Revit's entry point. Registers the ribbon
  button and an ExternalEvent the modeless dialog uses to call back into the
  Revit API thread.
- **`src/HangerLayoutCommand.cs`** — what runs when you click the button.
  Just constructs and shows the dialog.
- **`src/UI/HangerLayoutDialog.xaml(.cs)`** — the WPF window. All the
  buttons / lists / event handlers.
- **`src/Models/HangerSpecModels.cs`** — the data model
  (`SupportSpec`, `SupportSpecRow`, enums).
- **`src/Revit/HangerPlacer.cs`** — the core placement algorithm. Most
  behaviour changes happen here.

---

## Making changes safely

A few rules of thumb that have saved the original author pain:

### Don't trust `XYZ.IsAlmostEqualTo` as a distance check

It does a component-wise comparison, not Euclidean. Always use:

```csharp
if (a.DistanceTo(b) < tolFt) { ... }
```

### Snapshot `ConnectorManager.Connectors` before iterating

The lazy enumeration isn't stable across calls — converting to a list
once avoids surprises:

```csharp
var conns = part.ConnectorManager.Connectors.Cast<Connector>().ToList();
```

### Connector wrappers don't `ReferenceEquals`

Even for the "same" connector returned twice from Revit. Compare by
`Origin` (with `DistanceTo` tolerance) or by `(Owner.Id, connector index)`.

### `PickObject(ObjectType.PointOnElement, filter)` needs `AllowReference`

In a custom `ISelectionFilter`, `AllowReference(reference, point)` must
return `true` for the filter to accept a reference-based pick. Easy
to miss.

### Test these scenarios after any placement-algorithm change

- Single straight, span > 2× spacing.
- Two-straight chain with a flange/coupling between them (joint piece
  length must be in the spacing, not added).
- Mixed round + rect duct chain (Round → Rect reducer in the middle).
- Selection seeded on a joint piece, not a straight.
- Start Node picked on the left vs right end of a chain.

---

## Ship a new release

For binary distribution (so non-developers can drop in the DLL without
building):

### 1. Bump the version

Update `<Version>` in `src/HangerLayout.csproj`.

### 2. Release build

```powershell
cd src
dotnet build -c Release
```

Output lands in `src/bin/Release/`.

### 3. Package

Make a ZIP containing:
- `HangerLayout.dll` (from `src/bin/Release/`)
- `HangerLayout.addin` (from `src/`)
- `LICENSE` (from repo root)
- `README.md` (from repo root)

Name it `HangerLayout-v{version}.zip`.

### 4. Tag and push

```powershell
git tag v1.0.0
git push origin v1.0.0
```

### 5. Create the GitHub Release

Via the GitHub UI or `gh release create v1.0.0 HangerLayout-v1.0.0.zip --title "v1.0.0" --notes "Release notes..."`.

---

## Code style

- **No comments unless the WHY is non-obvious.** Identifier names should
  carry the WHAT.
- **`/// <summary>` XML docs** on public types and members that aren't
  self-explanatory.
- **British vs American English** — the original author's convention
  inherited from the parent project is "British in class names + ExtensibleStorage
  field keys, American in user-facing strings" (e.g. "Labour" in
  internal classes, "Labor" in dialogs). Hanger Layout doesn't really
  exercise this but the convention's there if it matters.

---

## Reporting issues

[File an issue](https://github.com/sbuchanan01/hanger-layout-for-revit/issues)
with:
- Revit version + build number (Help → About Revit IDE)
- What you did, what you expected, what happened
- Stack trace if there was a TaskDialog
- A minimal sample model if the bug is data-dependent (e.g. specific
  duct configuration)
