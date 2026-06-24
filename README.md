# Hanger Layout for Revit

A Revit add-in (builds for **Revit 2025** and **Revit 2026**) that places
pipe and duct hangers along selected runs of **Autodesk Fabrication parts**,
using size-banded spacing rules you define once and reuse across the project.

The dialog is modeless — you can pick parts in the model while it's open,
edit your specifications inline, and apply them without closing the window.
Specifications are stored on the Revit project, so they survive save/close
cycles and travel with the model.

![Hanger Layout dialog](docs/screenshots/dialog-overview.png)

---

## ⚠ Disclaimer

This code is provided by Autodesk for evaluation purposes only, as an example
of what is possible with the Autodesk platform and APIs. **THIS CODE IS NOT
INTENDED FOR USE IN PRODUCTION.** Autodesk makes no representations,
warranties, or commitments about the code. This code is not fully tested
and may include errors or faults that may cause total data loss or system
failure. No further updates to this tool are promised or implied — the
version published here may be the last, and may never be revised after the
posting date.

The MIT license applies to the source — see [LICENSE](LICENSE) — but the
evaluation-only nature above takes precedence over any "use however you
like" reading of the MIT terms.

---

## What it does

- **Build size-banded spec tables for pipes and ducts** — e.g. "0–2 in: 6 ft
  spacing, 1 ft from fittings, 6 in from joints".
- **Apply to a selection** — picks one button per service per shape (Round
  duct vs Rectangular duct vs Pipe), walks the selected straight runs end-
  to-end, and drops hangers at the right intervals.
- **Chain-spanning placement** — when a long run crosses joints (couplings,
  flanges, welds), hangers space correctly across the joints, accounting
  for the joint piece's own length.
- **Hanger override** — pick a specific hanger button per spec, or let the
  tool auto-pick the first compatible one for the part's shape.
- **Import from Fabrication Config** — read your existing `HSpecs.MAP`
  Hanger Specifications straight out of the active Fab database. Merge with
  what you already have, or replace.

---

## Install (no compiling required)

1. **Download the ZIP that matches your Revit version** from
   <https://github.com/sbuchanan01/hanger-layout-for-revit/releases>:
   - `HangerLayout-Revit2025-v1.0.0.zip` for Revit 2025
   - `HangerLayout-Revit2026-v1.0.0.zip` for Revit 2026
2. Extract `HangerLayout.dll` and `HangerLayout.addin`.
3. Drop **both files** into your version-matched Revit add-ins folder:
   - Revit 2025 → `%APPDATA%\Autodesk\Revit\Addins\2025\`
   - Revit 2026 → `%APPDATA%\Autodesk\Revit\Addins\2026\`

   (paste either path into File Explorer's address bar — it expands to
   your user folder.)
4. Restart Revit. You'll see a new **Hanger Layout** ribbon tab.

If Revit blocks the DLL on first launch with a security warning, right-click
`HangerLayout.dll` → **Properties** → tick **Unblock** at the bottom → OK.
That's a one-time Windows quirk for DLLs downloaded from the internet.

Full step-by-step with screenshots: [docs/installation.md](docs/installation.md).

---

## Quick start

1. Open a Revit model that contains Fabrication parts (pipes or ducts).
2. On the **Hanger Layout** tab, click **Hanger Layout**.
3. Add a spec row to **Pipe Specs** or **Duct Specs** — e.g. "Up to 6 in,
   spacing 10 ft, fitting setback 1 ft, joint setback 6 in".
4. Click **Save Specs**.
5. In **Hanger Placement**, pick a Service (Pipe Type / Hanger button), then
   **Apply** with parts selected.

Full user guide: [docs/user-guide.md](docs/user-guide.md).

---

## Modify the code

The repo is a standard .NET 8 / C# 12 project. Any compatible toolchain
works:

- **Visual Studio 2022 / 2026 Community** (free) — open `src/HangerLayout.csproj`.
- **JetBrains Rider** — open the same csproj.
- **VS Code + C# Dev Kit** — same.
- **Claude Code** — open the repo root; the included `CLAUDE.md` orients
  it to the project layout.
- **Anything else that speaks `dotnet build`** — `cd src && dotnet build -c Debug`.

Full build / debug / deploy guide: [docs/developer-guide.md](docs/developer-guide.md).

A code-structure tour for people modifying it:
[docs/architecture.md](docs/architecture.md).

---

## License

[MIT](LICENSE) — modify, redistribute, fork freely, just keep the copyright
notice and disclaimer. See the LICENSE file for the full text including the
Autodesk evaluation disclaimer.

---

## Acknowledgements

Built against the **Revit 2026** and **Autodesk Fabrication MEP** APIs.
