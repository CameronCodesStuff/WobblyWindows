// WobblyWindows v2 — soft-body wobbly window dragging for Windows 11
//
// Physics model (the important bit):
//   The window is simulated as FOUR corner masses joined by structural
//   springs (4 edges + 2 diagonals) — a tiny soft-body lattice, like a
//   simplified Compiz mesh.
//
//   * The corner(s) nearest your grab point get a STIFF "lead" spring toward
//     the cursor target → the grabbed section leads the motion immediately.
//   * The far corners get a SOFT "trail" spring → the rest of the window
//     lags behind, stretching toward the lead.
//   * Structural springs transmit motion between corners at finite speed →
//     direction changes make different areas catch up at different rates,
//     producing ripples across the surface.
//   * Low damping ratio → on release the shape overshoots its resting
//     position, then rebounds with progressively smaller oscillations
//     before settling exactly on target.
//
//   Each frame the real window rect is fitted to the deformed quad
//   (edge-midpoint averaging), so the actual app window stretches,
//   compresses and wobbles. Win32 can't shear other apps' windows, but this
//   is the closest physical approximation — and it feels like soft rubber.
//
// This is a TRAY-ONLY app: no window opens. Look for the icon in the system
// tray (bottom-right, near the clock — check the ^ hidden-icons flyout).
//
// Two ways to wobble:
//   1. Drag any title bar / drag region (Chrome tab strip, Discord top bar).
//   2. Hold Ctrl+Alt and drag ANYWHERE on a window — guaranteed fallback.
//
// Diagnostics: %LOCALAPPDATA%\WobblyWindows\wobbly.log (tray → Open log)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WobblyWindows;

static class Program
{
    [STAThread]
    static void Main()
    {
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Log.Write("=== WobblyWindows v2 starting ===");
        using var tray = new TrayApp();
        Application.Run();
        Log.Write("=== exiting ===");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tunables — tweak to taste
// ─────────────────────────────────────────────────────────────────────────────
static class Config
{
    // Stiffness of the spring pulling the GRABBED corner to the cursor.
    // High = the grabbed section leads almost immediately (slight delay).
    public const double LeadStiffness = 1500.0;

    // Stiffness pulling the FARTHEST corners along. Low = they trail behind,
    // stretching toward the lead. The gap between these two numbers is what
    // creates the "soft rubber" feel.
    public const double TrailStiffness = 300.0;

    // Springs between corners (edges + diagonals). Higher = ripples travel
    // faster across the surface and the shape recovers its rectangle sooner.
    public const double StructuralStiffness = 950.0;

    // Damping ratio. 1.0 = no wobble at all; ~0.25 = overshoot + a few
    // progressively smaller rebounds before settling (the Compiz feel).
    public const double DampingRatio = 0.25;

    // Extra global velocity damping (per second) — kills residual jitter.
    public const double GlobalDamping = 1.2;

    // Max stretch/compression of the real window, as a fraction of its base
    // size (0.28 = up to 28%). The physics is unclamped; only the rect is.
    public const double StretchMax = 0.28;

    // Integration: physics ticks every TickMs, subdivided into substeps of at
    // most MaxSubstep seconds for stability at high stiffness.
    public const int TickMs = 4;
    public const double MaxSubstep = 0.004;

    // Don't resize the real window for changes smaller than this (px) —
    // avoids hammering apps with 1px relayouts.
    public const int MinResizeDelta = 2;

    // Edge snap threshold on release (px from monitor edge)
    public const int SnapThreshold = 6;

    // Settle thresholds: every corner within this distance of its rest spot
    // and slower than this velocity → land exactly and stop.
    public const double SettleVelocity = 22.0;   // px/s
    public const double SettleDistance = 0.8;    // px

    // Hit-test reply timeout (ms) — keep small, it runs inside the mouse hook
    public const uint HitTestTimeoutMs = 60;
}

// ─────────────────────────────────────────────────────────────────────────────
// Async file logger (never blocks the mouse hook)
// ─────────────────────────────────────────────────────────────────────────────
static class Log
{
    public static readonly string FilePath;
    static readonly ConcurrentQueue<string> _queue = new();
    static readonly AutoResetEvent _signal = new(false);

    static Log()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WobblyWindows");
        try { Directory.CreateDirectory(dir); } catch { }
        FilePath = Path.Combine(dir, "wobbly.log");
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
                File.Delete(FilePath); // keep the log small
        }
        catch { }

        var t = new Thread(Drain) { IsBackground = true, Name = "LogWriter" };
        t.Start();
    }

    public static void Write(string message)
    {
        _queue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {message}");
        _signal.Set();
    }

    static void Drain()
    {
        var sb = new StringBuilder();
        while (true)
        {
            _signal.WaitOne(2000);
            sb.Clear();
            while (_queue.TryDequeue(out var line)) sb.AppendLine(line);
            if (sb.Length > 0)
            {
                try { File.AppendAllText(FilePath, sb.ToString()); } catch { }
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tray UI
// ─────────────────────────────────────────────────────────────────────────────
sealed class TrayApp : IDisposable
{
    readonly NotifyIcon _icon;
    readonly MouseHook _hook;
    readonly WobbleEngine _engine;

    public TrayApp()
    {
        _engine = new WobbleEngine();
        _hook = new MouseHook(_engine);

        var menu = new ContextMenuStrip();

        var enabled = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) =>
        {
            _hook.Enabled = enabled.Checked;
            if (enabled.Checked) _hook.Reinstall(); // also recovers a dropped hook
        };

        var stretch = new ToolStripMenuItem("Elastic stretch") { Checked = _engine.StretchEnabled, CheckOnClick = true };
        stretch.CheckedChanged += (_, _) => _engine.StretchEnabled = stretch.Checked;

        var snap = new ToolStripMenuItem("Edge snap on release") { Checked = _engine.SnapEnabled, CheckOnClick = true };
        snap.CheckedChanged += (_, _) => _engine.SnapEnabled = snap.Checked;

        var openLog = new ToolStripMenuItem("Open log file");
        openLog.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
            catch { }
        };

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { Dispose(); Application.Exit(); };

        menu.Items.Add(enabled);
        menu.Items.Add(stretch);
        menu.Items.Add(snap);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openLog);
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "WobblyWindows — drag a title bar, or Ctrl+Alt+drag anywhere",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _hook.Install();

        // Make it obvious the app is alive and where it lives
        _icon.BalloonTipTitle = "WobblyWindows is running";
        _icon.BalloonTipText =
            "No window opens — I live in the system tray (bottom-right, near the clock; " +
            "check the ^ hidden-icons flyout). Drag any title bar to wobble, " +
            "or hold Ctrl+Alt and drag anywhere on a window.";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(6000);
    }

    // Prefer the project logo (assets/app.ico) shipped next to the exe;
    // fall back to a generated jelly icon.
    static Icon LoadAppIcon()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "assets", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico"),
        })
        {
            try { if (File.Exists(candidate)) return new Icon(candidate); }
            catch { }
        }
        return MakeFallbackIcon();
    }

    static Icon MakeFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(255, 0, 229, 255));
            using var bar = new SolidBrush(Color.FromArgb(255, 10, 10, 26));
            g.FillClosedCurve(body, new[]
            {
                new Point(4, 8), new Point(28, 5), new Point(27, 26), new Point(5, 28)
            }, System.Drawing.Drawing2D.FillMode.Winding, 0.9f);
            g.FillClosedCurve(bar, new[]
            {
                new Point(6, 9), new Point(26, 7), new Point(26, 12), new Point(6, 14)
            }, System.Drawing.Drawing2D.FillMode.Winding, 0.9f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _hook.Dispose();
        _engine.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Low-level mouse hook: detects caption drags and takes them over
// ─────────────────────────────────────────────────────────────────────────────
sealed class MouseHook : IDisposable
{
    public bool Enabled = true;

    readonly WobbleEngine _engine;
    readonly Native.LowLevelMouseProc _proc; // keep delegate alive!
    IntPtr _hookHandle = IntPtr.Zero;
    readonly int _ownPid = Environment.ProcessId;

    // double-click forwarding
    long _lastDownTick;
    IntPtr _lastDownWindow;
    Point _lastDownPoint;

    static readonly string[] ExcludedClasses =
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "NotifyIconOverflowWindow", "TaskListThumbnailWnd",
        "TopLevelWindowForOverflowXamlIsland", "Xaml_WindowedPopupClass",
    };

    public MouseHook(WobbleEngine engine)
    {
        _engine = engine;
        _proc = HookProc;
    }

    public void Install()
    {
        // user32's module handle is the most reliable hMod for LL hooks,
        // especially under single-file publish where the exe module can be odd.
        IntPtr user32 = Native.LoadLibrary("user32.dll");
        _hookHandle = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, user32, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Log.Write($"HOOK INSTALL FAILED, Win32 error {err}");
            MessageBox.Show(
                $"Failed to install the mouse hook (Win32 error {err}).\n" +
                "Some antivirus/anti-cheat software blocks low-level hooks.",
                "WobblyWindows", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Log.Write("Mouse hook installed OK");
    }

    public void Reinstall()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        Install();
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;

                switch (msg)
                {
                    case Native.WM_LBUTTONDOWN:
                        if (OnButtonDown(info.pt))
                            return (IntPtr)1; // swallow — we own this drag now
                        break;

                    case Native.WM_MOUSEMOVE:
                        if (_engine.Dragging)
                            _engine.UpdateCursor(info.pt);
                        break;

                    case Native.WM_LBUTTONUP:
                        if (_engine.Dragging)
                        {
                            _engine.EndDrag(info.pt);
                            return (IntPtr)1; // pair with the swallowed down
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Hook exception: " + ex);
            }
        }
        return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    bool OnButtonDown(Point pt)
    {
        IntPtr hit = Native.WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return false;

        IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (root == IntPtr.Zero || !Native.IsWindowVisible(root)) return false;

        Native.GetWindowThreadProcessId(root, out uint pid);
        if (pid == _ownPid) return false;

        string cls = Native.GetClassNameSafe(root);
        foreach (var ex in ExcludedClasses)
            if (string.Equals(cls, ex, StringComparison.OrdinalIgnoreCase)) return false;

        if (Native.IsZoomed(root) || Native.IsIconic(root))
        {
            Log.Write($"click on '{cls}' skipped (maximized/minimized) — native drag");
            return false;
        }

        // Fallback trigger: Ctrl+Alt held → wobble-drag from anywhere on the
        // window, no hit-testing needed. Guaranteed to work.
        bool combo = Native.IsKeyDown(Native.VK_CONTROL) && Native.IsKeyDown(Native.VK_MENU);

        int hitCode = -1;
        if (!combo)
        {
            // Ask the window chain what's under the cursor. HTCAPTION covers
            // native title bars *and* custom drag regions (Chrome tab strip,
            // Discord's Electron drag area, etc.). Some apps only answer
            // correctly on the child under the cursor, others only on the
            // root — try the child first, then the root.
            if (hit != root && Native.TryHitTest(hit, pt, out int childHit)
                && childHit == Native.HTCAPTION)
            {
                hitCode = childHit;
            }
            else if (Native.TryHitTest(root, pt, out int rootHit))
            {
                hitCode = rootHit;
            }
            else
            {
                Log.Write($"click on '{cls}': hit-test FAILED (timeout/UIPI — elevated app?) — native drag. " +
                          "Tip: Ctrl+Alt+drag still works, or run WobblyWindows as admin.");
                return false;
            }

            if (hitCode != Native.HTCAPTION)
            {
                // Normal client-area click; stay out of the way. Logged so the
                // log shows what the app answered when you clicked a title bar.
                Log.Write($"click on '{cls}': hit={hitCode} (not caption) — passed through");
                return false;
            }
        }

        // Double-click on caption → forward as maximize toggle instead of drag
        long now = Environment.TickCount64;
        if (!combo
            && root == _lastDownWindow
            && now - _lastDownTick <= Native.GetDoubleClickTime()
            && Math.Abs(pt.X - _lastDownPoint.X) <= SystemInformation.DoubleClickSize.Width
            && Math.Abs(pt.Y - _lastDownPoint.Y) <= SystemInformation.DoubleClickSize.Height)
        {
            _lastDownTick = 0;
            _engine.Cancel();
            Native.PostMessage(root, Native.WM_NCLBUTTONDBLCLK,
                (IntPtr)Native.HTCAPTION, Native.MakeLParam(pt.X, pt.Y));
            Log.Write($"double-click on '{cls}' forwarded (maximize toggle)");
            return true;
        }

        _lastDownTick = now;
        _lastDownWindow = root;
        _lastDownPoint = pt;

        Log.Write($"BEGIN drag on '{cls}' ({(combo ? "Ctrl+Alt combo" : $"hit={hitCode}")})");
        _engine.BeginDrag(root, pt);
        return true;
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Soft-body physics: 4 corner masses + structural springs
// ─────────────────────────────────────────────────────────────────────────────
sealed class WobbleEngine : IDisposable
{
    public volatile bool StretchEnabled = true;
    public volatile bool SnapEnabled = true;
    public bool Dragging { get { lock (_gate) return _dragging; } }

    const int TL = 0, TR = 1, BR = 2, BL = 3;

    // structural lattice: 4 edges + 2 diagonals
    static readonly (int a, int b)[] SpringPairs =
    {
        (TL, TR), (TR, BR), (BR, BL), (BL, TL),
        (TL, BR), (TR, BL),
    };

    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _shutdown;

    // ── state (guarded by _gate) ─────────────────────────────────────────────
    bool _dragging, _active;
    IntPtr _hwnd;

    readonly double[] _px = new double[4], _py = new double[4]; // corner positions
    readonly double[] _vx = new double[4], _vy = new double[4]; // corner velocities
    readonly double[] _k = new double[4];                       // per-corner follow stiffness
    readonly double[] _c = new double[4];                       // per-corner damping
    readonly double[] _restLen = new double[SpringPairsCount];  // spring rest lengths
    readonly double[] _offX = new double[4], _offY = new double[4]; // rest offsets from top-left

    const int SpringPairsCount = 6;

    double _tx, _ty;                 // ideal (rest) top-left = cursor - grab offset
    int _grabDx, _grabDy;
    int _baseW, _baseH;
    int _lastW, _lastH;              // last applied size (resize hysteresis)
    Point _releaseCursor;
    bool _pendingSnapCheck;
    bool _loggedMoveFailure;

    public WobbleEngine()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "WobblePhysics" };
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    public void BeginDrag(IntPtr hwnd, Point cursor)
    {
        if (!Native.GetWindowRect(hwnd, out var rc))
        {
            Log.Write("BeginDrag: GetWindowRect failed");
            return;
        }

        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w < 1 || h < 1) return;

        lock (_gate)
        {
            _hwnd = hwnd;
            _baseW = w; _baseH = h;
            _lastW = w; _lastH = h;
            _grabDx = cursor.X - rc.Left;
            _grabDy = cursor.Y - rc.Top;
            _tx = rc.Left; _ty = rc.Top;

            // corner rest offsets from the window's top-left
            _offX[TL] = 0; _offY[TL] = 0;
            _offX[TR] = w; _offY[TR] = 0;
            _offX[BR] = w; _offY[BR] = h;
            _offX[BL] = 0; _offY[BL] = h;

            for (int i = 0; i < 4; i++)
            {
                _px[i] = rc.Left + _offX[i];
                _py[i] = rc.Top + _offY[i];
                _vx[i] = 0; _vy[i] = 0;
            }

            // Per-corner "follow" stiffness: near the grab → LeadStiffness
            // (leads immediately), far corners → TrailStiffness (trail behind
            // and stretch). Distance measured in the unit square so window
            // aspect ratio doesn't skew the feel.
            double gu = Math.Clamp(_grabDx / (double)w, 0, 1);
            double gv = Math.Clamp(_grabDy / (double)h, 0, 1);
            ReadOnlySpan<double> cu = stackalloc double[] { 0, 1, 1, 0 };
            ReadOnlySpan<double> cv = stackalloc double[] { 0, 0, 1, 1 };
            for (int i = 0; i < 4; i++)
            {
                double d = Math.Sqrt((cu[i] - gu) * (cu[i] - gu) + (cv[i] - gv) * (cv[i] - gv));
                double t = Math.Clamp(d / Math.Sqrt(2.0), 0, 1); // 0 at grab, 1 at farthest possible
                _k[i] = Config.LeadStiffness + (Config.TrailStiffness - Config.LeadStiffness) * t;
                _c[i] = 2.0 * Config.DampingRatio * Math.Sqrt(_k[i]);
            }

            // structural rest lengths from the base rectangle
            for (int s = 0; s < SpringPairsCount; s++)
            {
                var (a, b) = SpringPairs[s];
                double dx = _offX[b] - _offX[a], dy = _offY[b] - _offY[a];
                _restLen[s] = Math.Sqrt(dx * dx + dy * dy);
            }

            _dragging = true;
            _active = true;
            _pendingSnapCheck = false;
            _loggedMoveFailure = false;
        }

        ForceForeground(hwnd);
        _wake.Set();
    }

    public void UpdateCursor(Point cursor)
    {
        lock (_gate)
        {
            if (!_dragging) return;
            _tx = cursor.X - _grabDx;
            _ty = cursor.Y - _grabDy;
        }
    }

    public void EndDrag(Point cursor)
    {
        lock (_gate)
        {
            if (!_dragging) return;
            _dragging = false;
            _tx = cursor.X - _grabDx;
            _ty = cursor.Y - _grabDy;
            _releaseCursor = cursor;
            _pendingSnapCheck = SnapEnabled;
        }
    }

    public void Cancel()
    {
        lock (_gate) { _dragging = false; _active = false; }
    }

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        double last = 0;

        while (!_shutdown)
        {
            bool active;
            lock (_gate) active = _active;

            if (!active)
            {
                _wake.WaitOne();
                sw.Restart();
                last = 0;
                continue;
            }

            double now = sw.Elapsed.TotalSeconds;
            double dt = Math.Min(now - last, 0.025);
            last = now;

            Tick(dt);
            Thread.Sleep(Config.TickMs);
        }
    }

    void Tick(double dt)
    {
        IntPtr hwnd;
        bool snapCheck;
        Point releaseCursor;

        lock (_gate)
        {
            if (!_active) return;
            hwnd = _hwnd;
            snapCheck = !_dragging && _pendingSnapCheck;
            releaseCursor = _releaseCursor;
            if (snapCheck) _pendingSnapCheck = false;
        }

        if (!Native.IsWindow(hwnd)) { Cancel(); return; }

        // Snap check fires once, right after release
        if (snapCheck && TryEdgeSnap(hwnd, releaseCursor)) { Cancel(); return; }

        bool settled;
        int x, y, w, h;
        uint sizeFlag;
        int finalX, finalY, baseW, baseH;

        lock (_gate)
        {
            if (!_active) return;

            Integrate(dt);
            settled = IsSettled();
            ComputeOutputRect(out x, out y, out w, out h, out sizeFlag);

            finalX = (int)Math.Round(_tx);
            finalY = (int)Math.Round(_ty);
            baseW = _baseW; baseH = _baseH;
        }

        if (settled)
        {
            // land exactly on target with original size — completely still
            Native.SetWindowPos(hwnd, IntPtr.Zero, finalX, finalY, baseW, baseH,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            Cancel();
            return;
        }

        bool ok = Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER |
            Native.SWP_ASYNCWINDOWPOS | sizeFlag);

        if (!ok && !_loggedMoveFailure)
        {
            _loggedMoveFailure = true;
            Log.Write($"SetWindowPos FAILED, Win32 error {Marshal.GetLastWin32Error()} " +
                      "(elevated window? run WobblyWindows as admin)");
        }
    }

    // Semi-implicit Euler with substepping for stability at high stiffness.
    // Called with _gate held.
    void Integrate(double dt)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(dt / Config.MaxSubstep));
        double hStep = dt / steps;

        Span<double> fx = stackalloc double[4];
        Span<double> fy = stackalloc double[4];

        for (int s = 0; s < steps; s++)
        {
            fx.Clear(); fy.Clear();

            // follow springs: each corner is pulled toward its rest position
            // in the ideal rect, with per-corner stiffness (lead vs trail)
            for (int i = 0; i < 4; i++)
            {
                double ix = _tx + _offX[i];
                double iy = _ty + _offY[i];
                fx[i] += _k[i] * (ix - _px[i]) - _c[i] * _vx[i];
                fy[i] += _k[i] * (iy - _py[i]) - _c[i] * _vy[i];
            }

            // structural springs: transmit motion corner-to-corner → ripples
            for (int sp = 0; sp < SpringPairsCount; sp++)
            {
                var (a, b) = SpringPairs[sp];
                double dx = _px[b] - _px[a];
                double dy = _py[b] - _py[a];
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-6) continue;
                double f = Config.StructuralStiffness * (dist - _restLen[sp]) / dist;
                fx[a] += f * dx; fy[a] += f * dy;
                fx[b] -= f * dx; fy[b] -= f * dy;
            }

            double decay = Math.Max(0.0, 1.0 - Config.GlobalDamping * hStep);
            for (int i = 0; i < 4; i++)
            {
                _vx[i] = (_vx[i] + fx[i] * hStep) * decay;
                _vy[i] = (_vy[i] + fy[i] * hStep) * decay;
                _px[i] += _vx[i] * hStep;
                _py[i] += _vy[i] * hStep;
            }
        }

        // safety: if physics ever blows up, teleport to rest
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(_px[i] - (_tx + _offX[i])) > 5000 ||
                Math.Abs(_py[i] - (_ty + _offY[i])) > 5000)
            {
                for (int j = 0; j < 4; j++)
                {
                    _px[j] = _tx + _offX[j]; _py[j] = _ty + _offY[j];
                    _vx[j] = 0; _vy[j] = 0;
                }
                break;
            }
        }
    }

    // Called with _gate held.
    bool IsSettled()
    {
        if (_dragging) return false;
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(_px[i] - (_tx + _offX[i])) >= Config.SettleDistance) return false;
            if (Math.Abs(_py[i] - (_ty + _offY[i])) >= Config.SettleDistance) return false;
            if (Math.Abs(_vx[i]) >= Config.SettleVelocity) return false;
            if (Math.Abs(_vy[i]) >= Config.SettleVelocity) return false;
        }
        return true;
    }

    // Fit the real (axis-aligned) window rect to the deformed corner quad.
    // Edge positions are the average of their two corners, which captures
    // stretch/compression while cancelling shear the OS can't display.
    // Called with _gate held.
    void ComputeOutputRect(out int x, out int y, out int w, out int h, out uint sizeFlag)
    {
        double left = (_px[TL] + _px[BL]) * 0.5;
        double right = (_px[TR] + _px[BR]) * 0.5;
        double top = (_py[TL] + _py[TR]) * 0.5;
        double bottom = (_py[BL] + _py[BR]) * 0.5;

        double cx = (left + right) * 0.5;
        double cy = (top + bottom) * 0.5;

        if (!StretchEnabled)
        {
            w = _baseW; h = _baseH;
            x = (int)Math.Round(cx - _baseW * 0.5);
            y = (int)Math.Round(cy - _baseH * 0.5);
            sizeFlag = Native.SWP_NOSIZE;
            return;
        }

        double minW = _baseW * (1.0 - Config.StretchMax), maxW = _baseW * (1.0 + Config.StretchMax);
        double minH = _baseH * (1.0 - Config.StretchMax), maxH = _baseH * (1.0 + Config.StretchMax);
        int wantW = (int)Math.Round(Math.Clamp(right - left, minW, maxW));
        int wantH = (int)Math.Round(Math.Clamp(bottom - top, minH, maxH));

        // resize hysteresis: skip sub-threshold size changes so apps aren't
        // hammered with 1px relayouts every 4ms
        if (Math.Abs(wantW - _lastW) < Config.MinResizeDelta &&
            Math.Abs(wantH - _lastH) < Config.MinResizeDelta)
        {
            w = _lastW; h = _lastH;
            sizeFlag = Native.SWP_NOSIZE;
        }
        else
        {
            w = wantW; h = wantH;
            _lastW = w; _lastH = h;
            sizeFlag = 0;
        }

        x = (int)Math.Round(cx - w * 0.5);
        y = (int)Math.Round(cy - h * 0.5);
    }

    // Minimal Aero-snap emulation: top edge = maximize, left/right = half snap
    static bool TryEdgeSnap(IntPtr hwnd, Point cursor)
    {
        IntPtr mon = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MONITORINFO.New();
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;

        var m = mi.rcMonitor;
        var wa = mi.rcWork;

        if (cursor.Y <= m.Top + Config.SnapThreshold)
        {
            Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
            return true;
        }
        if (cursor.X <= m.Left + Config.SnapThreshold)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, wa.Left, wa.Top,
                (wa.Right - wa.Left) / 2, wa.Bottom - wa.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return true;
        }
        if (cursor.X >= m.Right - 1 - Config.SnapThreshold)
        {
            int half = (wa.Right - wa.Left) / 2;
            Native.SetWindowPos(hwnd, IntPtr.Zero, wa.Left + half, wa.Top,
                (wa.Right - wa.Left) - half, wa.Bottom - wa.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return true;
        }
        return false;
    }

    // SetForegroundWindow is restricted; the ALT-tap trick reliably unlocks it
    static void ForceForeground(IntPtr hwnd)
    {
        if (!Native.SetForegroundWindow(hwnd))
        {
            Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.SetForegroundWindow(hwnd);
        }
    }

    public void Dispose()
    {
        _shutdown = true;
        Cancel();
        _wake.Set();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Win32 interop
// ─────────────────────────────────────────────────────────────────────────────
static class Native
{
    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCLBUTTONDBLCLK = 0x00A3;
    public const int HTCAPTION = 2;
    public const uint GA_ROOT = 2;
    public const int SW_MAXIMIZE = 3;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public Point pt;
        public uint mouseData, flags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public static MONITORINFO New() => new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point p);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    public static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    public static string GetClassNameSafe(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool TryHitTest(IntPtr hwnd, Point pt, out int hitCode)
    {
        hitCode = 0;
        IntPtr ok = SendMessageTimeout(hwnd, WM_NCHITTEST, IntPtr.Zero,
            MakeLParam(pt.X, pt.Y), SMTO_ABORTIFHUNG, Config.HitTestTimeoutMs, out IntPtr result);
        if (ok == IntPtr.Zero) return false;
        hitCode = (int)(long)result;
        return true;
    }
}
