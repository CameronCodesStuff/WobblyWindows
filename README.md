# WobblyWindows

Compiz-style **wobbly windows** for Windows 11. Drag any real app window — Chrome, Discord, Explorer, VS Code, whatever — and it lags behind the cursor, overshoots, jiggles and settles like jelly. Optional squash & stretch deforms the window with velocity for extra bounce.

No frameworks, no dependencies, one C# source file + Win32.

## Run it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or just the Desktop Runtime if you already have a built exe).

```
dotnet run -c Release
```

or build a single exe:

```
build.bat        →  dist\WobblyWindows.exe
```

It lives in the system tray. Right-click the tray icon to toggle:

- **Enabled** — master switch
- **Squash & stretch** — velocity-based deformation (resizes the window live; turn off if an app relayouts badly)
- **Edge snap on release** — drop at the top edge to maximize, left/right edge for half-snap

## How it works

1. A `WH_MOUSE_LL` hook watches every left-click. It sends `WM_NCHITTEST` to the window under the cursor — if the answer is `HTCAPTION` (native title bars **and** custom drag regions like Chrome's tab strip or Discord's Electron drag area), the click is swallowed and the native drag never starts.
2. WobblyWindows takes over: a physics thread runs an under-damped spring (`x'' = k(target − x) − c·x'`, semi-implicit Euler at ~250 Hz) pulling the window toward `cursor − grab offset` via `SetWindowPos`.
3. On release the spring keeps oscillating until velocity and displacement drop below thresholds, then the window lands exactly on target at its original size.

## Tuning

All constants live in `Config` at the top of `src/Program.cs`:

| Constant | Effect |
|---|---|
| `Stiffness` | Higher = snappier, lower = floatier drag |
| `DampingRatio` | 1.0 = no wobble, 0.2 = very jiggly (default 0.30) |
| `SquashPerVelocity`, `SquashMax` | How much the window deforms with speed |
| `SnapThreshold` | Px from screen edge that triggers snap on release |

## Limitations (honest ones)

- **Elevated apps**: a normal-privilege hook can't hit-test admin windows (Task Manager, elevated terminals) — they drag natively. Run WobblyWindows as admin if you want them wobbly too.
- **Maximized windows** drag natively (restoring-while-wobbling is a mess, so it's skipped).
- **Esc-to-cancel** during a drag isn't supported (that's a native move-loop feature).
- Windows' own snap layouts / shake-to-minimize don't trigger during a wobbly drag; the built-in edge snap covers the common cases.
- Squash & stretch live-resizes the window every frame — most apps handle it fine, heavy ones may stutter, so it's toggleable.

## Uninstall

It's a portable exe. Exit from the tray, delete the folder. Nothing is written to the registry.
