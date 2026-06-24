# Installation

Two paths, depending on whether you want to **just use the tool** or **build
it from source**.

---

## A. Just use the tool (no compiling)

### 1. Download

Go to the [Releases page](https://github.com/sbuchanan01/hanger-layout-for-revit/releases)
and download the ZIP attachment **matching your Revit version**:

- `HangerLayout-Revit2025-v1.0.0.zip` — for Revit 2025
- `HangerLayout-Revit2026-v1.0.0.zip` — for Revit 2026

![Download from Releases](screenshots/install-download.png)

### 2. Extract

Unzip anywhere. Inside you'll find:

```
HangerLayout.dll
HangerLayout.addin
```

### 3. Drop into the Revit Add-ins folder

Press <kbd>Win</kbd>+<kbd>R</kbd> to open the Run dialog, paste **the path
that matches the ZIP you downloaded**, hit <kbd>Enter</kbd>:

```
%APPDATA%\Autodesk\Revit\Addins\2025
%APPDATA%\Autodesk\Revit\Addins\2026
```

That opens the per-user add-ins folder for your Revit version.

> If the folder doesn't exist, create it. The structure is:
> `%APPDATA%\Autodesk\Revit\Addins\2026\` (you'll already have an
> `Addins` folder with other version sub-folders if you've installed
> add-ins before).

Copy **both** files into that folder:

![Files dropped in Addins folder](screenshots/install-addins-folder.png)

### 4. Unblock the DLL (Windows quirk)

Windows marks DLLs downloaded from the internet as "blocked" by default —
Revit will refuse to load them. Right-click `HangerLayout.dll` →
**Properties** → at the bottom of the General tab, tick **Unblock** → OK.

![Unblock the DLL](screenshots/install-unblock.png)

If you don't see an Unblock checkbox, the file is already cleared — skip.

### 5. Launch Revit

Start Revit. You'll see a new **Hanger Layout** ribbon tab with a
**Hanger Layout** button.

![Ribbon tab](screenshots/install-ribbon.png)

If the tab doesn't appear, see [Troubleshooting](#troubleshooting) below.

---

## B. Build from source

Prerequisites:

- **Windows 10/11**
- **Revit 2025 or 2026** (full install — the add-in references DLLs from
  the install folder)
- **.NET 8 SDK** — [download from Microsoft](https://dotnet.microsoft.com/download)
- Git — [download](https://git-scm.com/download/win) or use GitHub Desktop

### 1. Clone the repo

```powershell
git clone https://github.com/sbuchanan01/hanger-layout-for-revit.git
cd hanger-layout-for-revit
```

### 2. Build

Default build targets **Revit 2026**:

```powershell
cd src
dotnet build -c Debug
```

To target **Revit 2025** instead, pass `-p:RevitVersion=2025`:

```powershell
dotnet build -c Debug -p:RevitVersion=2025
```

Output goes into a per-version sub-folder (e.g. `bin/Debug-Revit2025/`)
so the two versions don't overwrite each other.

If Revit installed somewhere other than `C:\Program Files\Autodesk\Revit <version>`,
override the path:

```powershell
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

### 3. Auto-deploy

Debug builds automatically copy `HangerLayout.dll` and `HangerLayout.addin`
to `%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\` (i.e. the folder
matching whatever `-p:RevitVersion` you built with — default 2026).
**Close Revit first** — if
Revit is open when you build, the DLL is locked and the copy step is
skipped (the build itself still succeeds; you just need to copy manually).

### 4. Launch Revit

Same as the install path — look for the **Hanger Layout** ribbon tab.

---

## Troubleshooting

### "I installed the files but the ribbon tab doesn't show up"

Check that **both** files are in
`%APPDATA%\Autodesk\Revit\Addins\<version>\` (where `<version>` matches
the Revit version you're starting — `2025` or `2026`):

- `HangerLayout.addin` (the manifest — without it, Revit doesn't know what
  to load)
- `HangerLayout.dll` (the actual add-in)

Open `HangerLayout.addin` in Notepad and confirm it points at
`HangerLayout.dll` (relative path). If you renamed either file, fix the
reference.

### "Revit shows a security warning about the DLL"

Right-click `HangerLayout.dll` → Properties → **Unblock** → OK. Restart
Revit.

### "I get a startup error dialog from the add-in"

The dialog title is "Hanger Layout — startup error". Copy the exception
text and either:
- Search [existing issues](https://github.com/sbuchanan01/hanger-layout-for-revit/issues), or
- File a new issue with the exception text + your Revit version.

### "The icon shows but clicking the button does nothing"

Most likely a missing dependency. Check the Revit journal log under
`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <version>\Journals\` —
search for `HangerLayout` and look for stack traces.

### "Build fails with 'Could not find RevitAPI.dll'"

Your Revit install path differs from the default. Pass `/p:RevitInstallPath`:

```powershell
dotnet build -c Debug /p:RevitInstallPath="D:\My Revit Folder"
```

### "I have an older or newer Revit version"

The add-in officially supports **Revit 2025** and **Revit 2026** — pre-
built ZIPs for both are on the Releases page, and the source builds
clean against both via `-p:RevitVersion=2025` or `-p:RevitVersion=2026`.

For other versions, you can try the build:

```powershell
dotnet build -c Debug -p:RevitVersion=2024
```

It may or may not compile cleanly — the Revit API surface has changed
between versions, but the APIs this add-in uses are mostly stable.
Revit 2024 (the last .NET Framework 4.8 version) is the most likely to
need source-level adjustment.

---

## Uninstalling

Delete `HangerLayout.dll` and `HangerLayout.addin` from
`%APPDATA%\Autodesk\Revit\Addins\<version>\` (whichever you installed
into) and restart Revit.

Saved hanger specs persist on the Revit project itself (ExtensibleStorage),
not in the add-in folder — they survive uninstall. If you want to clear
them, reinstall the add-in, open the dialog, delete the rows, and Save.
