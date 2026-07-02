# WobblyWindows

Compiz-style **wobbly windows** for Windows 11. Drag any real app window — Chrome, Discord, Explorer, VS Code — and it lags behind the cursor, overshoots, jiggles and settles like jelly.

No frameworks, no dependencies, one C# source file + Win32.

---

## Where does it appear?

**Nothing opens. There is no window.** WobblyWindows is a background app that lives in the **system tray** — the little icon area in the **bottom-right corner of your taskbar, next to the clock**.

1. Run it (see below). A **notification balloon** pops up confirming it started.
2. Look next to the clock for a **cyan jelly icon**. Windows 11 hides most tray icons by default — click the **`^` (Show hidden icons)** arrow to find it.
3. Right-click the icon for the menu (Enabled, Squash & stretch, Edge snap, Open log file, Exit).

If you see the icon, it's running. If nothing wobbles, jump to **Troubleshooting** below.

## Running it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run -c Release
```

or build a portable single exe:

```
build.bat        →  dist\WobblyWindows.exe   (double-click that)
```

## How to wobble (2 ways)

1. **Drag a title bar** — the normal way. Works on native title bars *and* custom drag regions (Chrome's tab strip, Discord's top bar). Grab it, fling it around, let go — it overshoots and jiggles before settling.
2. **Hold `Ctrl+Alt` and drag anywhere on a window** — the guaranteed fallback. This skips title-bar detection completely, so it works even on apps with weird custom frames.

Quick test: open **Notepad**, grab its title bar, and drag fast in a circle. That's the baseline — if Notepad wobbles, the engine works and any app that doesn't is a detection issue (use Ctrl+Alt+drag on it).

## Troubleshooting — "it's running but nothing wobbles"

WobblyWindows logs every decision it makes to:

```
%LOCALAPPDATA%\WobblyWindows\wobbly.log
```

(Or just tray icon → **Open log file**.) Click a title bar once, then check the last lines:

| Log says | Meaning | Fix |
|---|---|---|
| `HOOK INSTALL FAILED` | Antivirus / anti-cheat blocked the mouse hook | Whitelist the exe, or close the anti-cheat |
| *nothing at all when you click* | Hook was silently dropped by Windows | Tray menu → untick then re-tick **Enabled** (this reinstalls the hook) |
| `hit=1 (not caption)` | The app told us that spot isn't a drag region | Grab the actual title bar, or use **Ctrl+Alt+drag** |
| `hit-test FAILED (timeout/UIPI — elevated app?)` | Target app runs as admin, we can't talk to it | Run WobblyWindows **as administrator** too |
| `SetWindowPos FAILED ... error 5` | Same — elevated target window | Run as administrator |
| `BEGIN drag ...` but no movement | Genuinely weird — send me the log | |
| `skipped (maximized)` | Maximized windows drag natively by design | Restore the window first |

Also worth knowing: if the exe was downloaded/copied, right-click → Properties → **Unblock** if the checkbox is there, since SmartScreen blocking can prevent it launching at all.

## How it works

1. A `WH_MOUSE_LL` hook watches every left-click. It hit-tests the window under the cursor (`WM_NCHITTEST`, tried on both the child window and its root) — if the answer is `HTCAPTION`, the click is swallowed and the native drag never starts. `Ctrl+Alt` bypasses the hit test.
2. **Soft-body physics**: the window is simulated as four corner masses joined by structural springs (4 edges + 2 diagonals) — a tiny Compiz-style lattice. The corner nearest your grab gets a stiff "lead" spring toward the cursor, so the grabbed section moves almost immediately; the far corners get a soft "trail" spring, so the rest lags and stretches toward the lead. The structural springs transmit motion between corners at finite speed, so direction changes ripple across the surface. Low damping means the shape overshoots on release and rebounds through a few shrinking oscillations before landing exactly on target, completely still.
3. Each frame (~250 Hz, semi-implicit Euler with substepping) the real window rect is fitted to the deformed quad — edge positions are the average of their two corners, which captures stretch/compression while cancelling the shear the OS can't display — and applied with `SetWindowPos`. Win32 can't mesh-deform other apps' windows, so this is the closest physical approximation.

## Tuning

All constants live in `Config` at the top of `src/Program.cs`:

| Constant | Effect |
|---|---|
| `LeadStiffness` | How immediately the grabbed section follows the cursor |
| `TrailStiffness` | How far the rest trails behind (bigger gap from Lead = more rubbery) |
| `StructuralStiffness` | How fast ripples travel across the surface / shape recovery speed |
| `DampingRatio` | 1.0 = no wobble, ~0.25 = overshoot + a few shrinking rebounds |
| `StretchMax` | Max window stretch/compression (fraction of base size) |
| `SnapThreshold` | Px from screen edge that triggers snap on release |

## Known limitations

- **Elevated apps** (Task Manager, admin terminals) only wobble if WobblyWindows itself runs as admin.
- **Maximized windows** drag natively (skipped on purpose).
- **Esc-to-cancel** mid-drag isn't supported (native move-loop feature).
- Windows' snap layouts don't trigger during a wobbly drag; the built-in edge snap (top = maximize, sides = half snap) covers the common cases.
- Elastic stretch live-resizes the window while it wobbles — heavy apps may stutter; toggle it off in the tray (you keep the lag/overshoot motion, just no size deformation).

## Uninstall

Portable exe. Exit from the tray, delete the folder, optionally delete `%LOCALAPPDATA%\WobblyWindows`. Nothing touches the registry.
