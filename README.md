# WobblyWindows

Compiz-style **wobbly windows** for Windows 11 — with real jelly deformation and a 3D tilt. Drag any app window (Chrome, Discord, Explorer, VS Code…) and its surface bends, stretches, ripples, leans into the motion, then rebounds and settles like soft rubber.

No frameworks, no dependencies, one C# source file + Win32.

---

## Where does it appear?

When you launch it, a **popup confirms it was installed and is running successfully** and explains everything below. There is no main window — WobblyWindows lives in the **system tray**, the icon area in the **bottom-right of the taskbar next to the clock**. Windows 11 hides new tray icons, so click the **`^` (Show hidden icons)** arrow to find it. Right-click the icon for the menu (Enabled, Jelly deformation, 3D tilt, Edge snap, Open log file, Exit).

## Running it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run -c Release
```

or build a portable single exe:

```
build.bat        →  dist\WobblyWindows.exe   (double-click that)
```

## Everything is wobbleable

- **Title bars & drag regions** — plain drag. Works on native title bars and custom ones (Chrome's tab strip, Discord's top bar).
- **Any window, from any point** — hold **Ctrl+Alt** and drag anywhere on it. No title bar needed.
- **Maximized windows** — grab the title bar and pull: the window restores under your cursor (native behavior) and wobbles from there.

Quick test: open Notepad, grab the title bar, fling it in a circle, let go.

## How it works (v3 architecture)

Windows can't bend another app's live window, and rapidly resizing a real window just looks like glitchy resizing. So WobblyWindows does what Compiz does:

1. **On grab**, a snapshot of the window is captured (`PrintWindow` with `PW_RENDERFULLCONTENT`) and the real window is made invisible **in place** (`WS_EX_LAYERED` alpha 0). It never moves during the drag.
2. The snapshot is drawn onto a deforming **6×6 soft-body mesh** (drawn as clipped triangle pairs per cell — exact affine maps, so no seams even under the 3D perspective) inside a click-through, per-pixel-alpha overlay (`UpdateLayeredWindow`). The grab point is **hard-pinned to the cursor** — zero lag at your hand — while every other mesh point trails on springs (stiff near the grab, loose far away), with structural springs (neighbors + cell diagonals) carrying ripples across the surface.
3. A **pseudo-3D tilt** is layered on top: the whole sheet rotates toward the motion via a perspective projection, and on release the tilt spring oscillates back — the window visibly wobbles in 3D.
4. When every mesh point has settled and the tilt has died out, the **real window is moved exactly once** to the final position, its alpha restored, and the overlay hidden. No resize thrash, no fighting the app.

If capture or transparency isn't possible on a particular window (already-layered apps, elevated apps without admin), it automatically falls back to a rigid spring-follow drag, so dragging always works.

## Settings

Right-click the tray icon → **Settings…** for a live control panel (saved to `%LOCALAPPDATA%\WobblyWindows\settings.json`):

- **Speed** — how fast the jelly reacts and settles
- **Wobble amount** — how many rebounds after release (0 = none)
- **Softness** — how rubbery the trailing stretch is
- **Max stretch** — cap on how far the surface can smear
- **Tilt angle** + 3D tilt on/off
- Toggles for jelly deformation, edge snap, and the startup welcome popup

Changes save automatically and apply to the next drag.

## Deep tuning

Internals not in the UI live in `Config` at the top of `src/Program.cs`:

| Constant | Effect |
|---|---|
| `FocalLength` | Smaller = more dramatic 3D perspective |
| `TiltPerVelocity`, `TiltDampingRatio` | Tilt responsiveness / post-release 3D wobble |
| `GridCells` | Mesh resolution (5 → 6×6 points) |
| `GlobalDamping`, `MaxSubstep`, `TickMs` | Simulation internals |

## Troubleshooting — "it's running but nothing wobbles"

Every decision is logged to `%LOCALAPPDATA%\WobblyWindows\wobbly.log` (tray → **Open log file**). Click a title bar once, check the last lines:

| Log says | Meaning | Fix |
|---|---|---|
| `HOOK INSTALL FAILED` | Antivirus / anti-cheat blocked the mouse hook | Whitelist the exe |
| *nothing at all when you click* | Hook silently dropped by Windows | Tray → untick & re-tick **Enabled** (reinstalls hook) |
| `hit=1 (not caption)` | That spot isn't a drag region | Grab the title bar, or **Ctrl+Alt+drag** |
| `hit-test FAILED (timeout/UIPI)` | Target app runs as admin | Run WobblyWindows as administrator |
| `rigid fallback drag …` | Capture/transparency unavailable for that window | Normal — that window still drags with spring physics, just no deformation |
| `PrintWindow returned false` | App refuses snapshot capture (DRM/protected) | Falls back to rigid automatically |
| `skipped (maximized/minimized)` shouldn't appear in v3 | — | Maximized windows now restore-and-wobble |

If the exe was downloaded/copied: right-click → Properties → **Unblock** if the checkbox is there.

## Known limitations

- **Elevated apps** (Task Manager, admin terminals) need WobblyWindows run as admin.
- During a drag the jelly shows a **snapshot** — a playing video inside the window freezes for the duration of the drag (Compiz shows live content; Win32 doesn't offer a warpable live surface).
- **Esc-to-cancel** mid-drag isn't supported.
- Windows' snap layouts don't trigger during a wobbly drag; the built-in edge snap (top = maximize, sides = half snap) covers the common cases.
- Very large windows are auto-downscaled for the snapshot to keep rendering fast.

## Uninstall

Portable exe. Exit from the tray, delete the folder, optionally delete `%LOCALAPPDATA%\WobblyWindows`. Nothing touches the registry.
