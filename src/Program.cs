// WobblyWindows v3 — real jelly deformation + 3D wobble for Windows 11
//
// Architecture (why v3 feels right where v2 felt wrong):
//   Windows can't bend another app's live window — and resizing a real
//   window rapidly (v2) just looks like glitchy resizing. So v3 does what
//   Compiz does:
//
//     1. On grab, a snapshot of the window is captured (PrintWindow) and the
//        real window is made invisible IN PLACE (alpha 0 — it never moves
//        during the drag).
//     2. The snapshot is drawn onto a deforming 5x5 MESH inside a
//        click-through, per-pixel-alpha overlay window. The mesh is a real
//        soft-body: the grab point is HARD-PINNED to the cursor (zero lag at
//        your hand = clean, precise feel) while every other point trails on
//        springs — the surface genuinely bends, stretches and ripples.
//     3. A pseudo-3D tilt is layered on top: the whole sheet rotates toward
//        the motion (perspective projection), and on release the tilt
//        oscillates back — the window visibly wobbles in 3D.
//     4. When everything settles, the REAL window is moved exactly once to
//        the final spot, its alpha restored, and the overlay hidden. No
//        resize thrash, no fighting the app.
//
// Everything is wobbleable:
//   * Title bars & drag regions (Chrome tab strip, Discord top bar) — plain drag.
//   * ANY window from ANY point — hold Ctrl+Alt and drag anywhere.
//   * Maximized windows — grabbing the title bar restores them under the
//     cursor (like native) and wobbles from there.
//
// If snapshot capture or transparency fails on a particular window (rare;
// e.g. already-layered or protected windows), it falls back to a rigid
// spring-follow so dragging always works.
//
// TRAY-ONLY app — a popup + balloon on launch tell you where it lives.
// Diagnostics: %LOCALAPPDATA%\WobblyWindows\wobbly.log (tray → Open log)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

        Settings.Load();
        Log.Write("=== WobblyWindows v3 starting ===");
        using var tray = new TrayApp();
        tray.ShowWelcome();
        Application.Run();
        Log.Write("=== exiting ===");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// User settings — persisted to %LOCALAPPDATA%\WobblyWindows\settings.json
// Sliders map to effective physics values below; Config keeps the internals.
// ─────────────────────────────────────────────────────────────────────────────
sealed class Settings
{
    public static Settings Current = new();

    // slider-backed (persisted)
    public int SpeedPercent { get; set; } = 100;      // 40..250 — how fast the jelly reacts/settles
    public int WobbleAmount { get; set; } = 75;       // 0..100  — rebound count/juiciness
    public int JellySoftness { get; set; } = 70;      // 0..100  — how rubbery the trailing stretch is
    public int TiltMaxDeg { get; set; } = 16;         // 0..30   — 3D lean angle
    public int MaxStretchPercent { get; set; } = 55;  // 20..90  — smear cap
    public bool DeformEnabled { get; set; } = true;
    public bool TiltEnabled { get; set; } = true;
    public bool SnapEnabled { get; set; } = true;
    public bool ShowWelcomeAtStartup { get; set; } = true;

    // effective physics values (speed scales stiffness quadratically so
    // response time scales linearly with the slider)
    [System.Text.Json.Serialization.JsonIgnore]
    public double Speed2 => Math.Pow(Math.Clamp(SpeedPercent, 40, 250) / 100.0, 2);
    [System.Text.Json.Serialization.JsonIgnore]
    public double DampingRatio => 0.60 - 0.50 * Math.Clamp(WobbleAmount, 0, 100) / 100.0;
    [System.Text.Json.Serialization.JsonIgnore]
    public double HomeStiffnessNear => 430.0 * Speed2;
    [System.Text.Json.Serialization.JsonIgnore]
    public double HomeStiffnessFar => Math.Max(40.0, 320.0 - 2.6 * Math.Clamp(JellySoftness, 0, 100)) * Speed2;
    [System.Text.Json.Serialization.JsonIgnore]
    public double StructuralStiffness => Math.Max(150.0, 700.0 - 3.2 * Math.Clamp(JellySoftness, 0, 100)) * Speed2;
    [System.Text.Json.Serialization.JsonIgnore]
    public double TiltStiffness => 110.0 * Speed2;
    [System.Text.Json.Serialization.JsonIgnore]
    public double MaxDisplacementFactor => Math.Clamp(MaxStretchPercent, 20, 90) / 100.0;
    [System.Text.Json.Serialization.JsonIgnore]
    public double RigidStiffness => 700.0 * Speed2;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WobblyWindows", "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null) Current = s;
            }
        }
        catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(Current,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
    }

    public void ResetToDefaults()
    {
        var d = new Settings();
        SpeedPercent = d.SpeedPercent; WobbleAmount = d.WobbleAmount;
        JellySoftness = d.JellySoftness; TiltMaxDeg = d.TiltMaxDeg;
        MaxStretchPercent = d.MaxStretchPercent;
        DeformEnabled = d.DeformEnabled; TiltEnabled = d.TiltEnabled;
        SnapEnabled = d.SnapEnabled; ShowWelcomeAtStartup = d.ShowWelcomeAtStartup;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tunables — internals not exposed in the settings UI
// ─────────────────────────────────────────────────────────────────────────────
static class Config
{
    // ── Jelly mesh ───────────────────────────────────────────────────────────
    // Grid resolution: cells per axis (5 → 6x6 points). More = smoother
    // bending, slightly more CPU.
    public const int GridCells = 5;

    // Spring pulling each mesh point back to its rest spot. Points near the
    // grab are stiffer (follow quickly), far points are looser (trail and
    // stretch). The bigger the gap, the more rubbery it feels.

    // Springs between neighboring mesh points — how fast ripples travel
    // across the surface and how strongly the sheet keeps its shape.

    // Damping ratio: 1.0 = no wobble; ~0.22 = lots of juicy rebounds.

    // Extra global velocity decay (per second) — kills residual micro-jitter.
    public const double GlobalDamping = 0.8;

    // Hard cap on how far any mesh point may stray from its rest spot,
    // as a fraction of the window's larger dimension.

    // ── 3D tilt ──────────────────────────────────────────────────────────────
    // The sheet rotates toward its motion and springs back with oscillation.       // max lean angle
    public const double TiltPerVelocity = 0.020; // degrees per px/s of drag speed   // tilt spring
    public const double TiltDampingRatio = 0.28; // low = 3D wobble on release
    public const double FocalLength = 1500.0;    // perspective strength (smaller = more dramatic)

    // ── Simulation / rendering ───────────────────────────────────────────────
    public const int TickMs = 5;                 // ~150 physics ticks/s
    public const double MaxSubstep = 0.004;
    public const double MaxSnapshotPixels = 1_800_000; // downscale huge windows
    public const int SettleVelocity = 20;        // px/s
    public const double SettleDistance = 1.2;    // px
    public const double SettleTiltRad = 0.006;

    // ── Fallback rigid mode (used only when capture fails) ──────────────────

    // ── Misc ─────────────────────────────────────────────────────────────────
    public const int SnapThreshold = 6;          // px from monitor edge
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
                File.Delete(FilePath);
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
// Click-through, per-pixel-alpha overlay the jelly is drawn onto
// ─────────────────────────────────────────────────────────────────────────────
sealed class OverlayForm : Form
{
    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT |
                          Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE |
                          Native.WS_EX_TOPMOST;
            return cp;
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
    readonly OverlayForm _overlay;

    public TrayApp()
    {
        _overlay = new OverlayForm();
        _ = _overlay.Handle; // force native handle creation on the UI thread

        _engine = new WobbleEngine(_overlay.Handle);
        _hook = new MouseHook(_engine);

        var menu = new ContextMenuStrip();

        var enabled = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) =>
        {
            _hook.Enabled = enabled.Checked;
            if (enabled.Checked) _hook.Reinstall(); // also recovers a dropped hook
        };

        var settings = new ToolStripMenuItem("Settings\u2026");
        settings.Click += (_, _) => SettingsForm.ShowSingleton();

        var openLog = new ToolStripMenuItem("Open log file");
        openLog.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
            catch { }
        };

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { Dispose(); Application.Exit(); };

        menu.Items.Add(enabled);
        menu.Items.Add(settings);
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
    }

    public void ShowWelcome()
    {
        if (!Settings.Current.ShowWelcomeAtStartup)
        {
            _icon.BalloonTipTitle = "WobblyWindows is running";
            _icon.BalloonTipText = "Drag any title bar to wobble, or Ctrl+Alt+drag anywhere. Right-click the tray icon for Settings.";
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
            return;
        }

        _icon.BalloonTipTitle = "WobblyWindows is running";
        _icon.BalloonTipText = "Drag any title bar to wobble, or hold Ctrl+Alt and drag anywhere on a window.";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);

        MessageBox.Show(
            "WobblyWindows was installed and is now running successfully!\n\n" +
            "WHERE IS IT?\n" +
            "It runs from the system tray — the icon area in the bottom-right of the " +
            "taskbar, next to the clock. Windows 11 hides new icons, so click the " +
            "^ (Show hidden icons) arrow to find the WobblyWindows icon.\n\n" +
            "HOW TO USE IT\n" +
            "•  Drag any window by its title bar — it bends, stretches and wobbles.\n" +
            "•  Hold Ctrl+Alt and drag ANYWHERE on ANY window — everything is wobbleable.\n" +
            "•  Maximized windows work too: grab the title bar and pull down.\n\n" +
            "Right-click the tray icon \u2192 Settings to tune speed, jelly softness and 3D tilt,\n" +
            "open the log, or exit. This popup can be turned off in Settings.",
            "WobblyWindows — installed successfully",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(255, 0, 229, 255));
            using var bar = new SolidBrush(Color.FromArgb(255, 10, 10, 26));
            g.FillClosedCurve(body, new[]
            {
                new Point(4, 8), new Point(28, 5), new Point(27, 26), new Point(5, 28)
            }, FillMode.Winding, 0.9f);
            g.FillClosedCurve(bar, new[]
            {
                new Point(6, 9), new Point(26, 7), new Point(26, 12), new Point(6, 14)
            }, FillMode.Winding, 0.9f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _hook.Dispose();
        _engine.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _overlay.Dispose();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Low-level mouse hook: detects grabs and takes over the drag
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

        // Fallback trigger: Ctrl+Alt held → wobble-drag from anywhere on any
        // window, no hit-testing needed. This is what makes EVERYTHING
        // wobbleable.
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
                Log.Write($"click on '{cls}': hit={hitCode} (not caption) — passed through");
                return false;
            }
        }

        bool zoomed = Native.IsZoomed(root);

        // Double-click on caption → forward as maximize toggle instead of drag
        long now = Environment.TickCount64;
        if (!combo && !zoomed
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

        Log.Write($"BEGIN drag on '{cls}' ({(combo ? "Ctrl+Alt combo" : $"hit={hitCode}")}{(zoomed ? ", was maximized" : "")})");
        _engine.BeginDrag(root, pt, zoomed);
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
// The engine: soft-body mesh + 3D tilt, rendered to the overlay
// ─────────────────────────────────────────────────────────────────────────────
sealed class WobbleEngine : IDisposable
{
    public bool Dragging { get { lock (_gate) return _dragging; } }

    enum Mode { Idle, Mesh, Rigid }

    const int N = Config.GridCells + 1;      // points per axis
    const int PointCount = N * N;

    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    readonly IntPtr _overlay;
    volatile bool _shutdown;

    // ── shared drag state (guarded by _gate) ─────────────────────────────────
    bool _dragging, _active, _needsSetup, _wasZoomed;
    IntPtr _hwnd;
    double _tx, _ty;                 // ideal (rest) top-left of the VISUAL rect
    int _grabDx, _grabDy;            // cursor offset inside the visual rect
    Point _grabCursor, _releaseCursor;
    bool _pendingSnapCheck;

    // ── engine-thread-only state ─────────────────────────────────────────────
    Mode _mode = Mode.Idle;
    int _visW, _visH;                          // visual rect size
    int _visOffX, _visOffY;                    // visual rect offset inside window rect
    double _prevTx, _prevTy;                   // for drag velocity (tilt)
    double _velEmaX, _velEmaY;

    // mesh
    readonly double[] _mpx = new double[PointCount], _mpy = new double[PointCount];
    readonly double[] _mvx = new double[PointCount], _mvy = new double[PointCount];
    readonly double[] _homeX = new double[PointCount], _homeY = new double[PointCount]; // offsets from _tx/_ty
    readonly double[] _kHome = new double[PointCount], _cHome = new double[PointCount];
    int _pin = -1;
    (int a, int b, double rest)[] _springs = Array.Empty<(int, int, double)>();
    double _maxDisp;
    double _structK;

    // 3D tilt (radians)
    double _tiltX, _tiltY, _tiltVX, _tiltVY;

    // rigid fallback
    double _rpx, _rpy, _rvx, _rvy;

    // engine-thread ownership of the in-flight window (Teardown must restore
    // THIS window even if a new BeginDrag has already overwritten shared state)
    IntPtr _curHwnd;
    double _lastTx, _lastTy;

    // capture / rendering
    Bitmap _snapshot;                // possibly downscaled
    double _snapScale = 1.0;
    Bitmap _canvas;
    bool _overlayShown;
    bool _madeTransparent;
    int _origExStyle;
    bool _loggedMoveFailure;

    readonly PointF[] _proj = new PointF[PointCount];

    public WobbleEngine(IntPtr overlayHandle)
    {
        _overlay = overlayHandle;
        _thread = new Thread(Loop) { IsBackground = true, Name = "WobblePhysics" };
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    // Called from the hook thread — must stay lightweight. All heavy setup
    // (restore-from-maximized, snapshot capture, overlay init) happens on the
    // engine thread so the hook never stalls.
    public void BeginDrag(IntPtr hwnd, Point cursor, bool wasZoomed)
    {
        lock (_gate)
        {
            _hwnd = hwnd;
            _grabCursor = cursor;
            _wasZoomed = wasZoomed;
            _dragging = true;
            _active = true;
            _needsSetup = true;
            _pendingSnapCheck = false;
        }
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
            _pendingSnapCheck = Settings.Current.SnapEnabled;
        }
    }

    public void Cancel()
    {
        lock (_gate) { _dragging = false; _active = false; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    void Loop()
    {
        var sw = Stopwatch.StartNew();
        double last = 0;

        while (!_shutdown)
        {
            bool active, needsSetup;
            lock (_gate) { active = _active; needsSetup = _needsSetup; }

            if (!active)
            {
                if (_mode != Mode.Idle) Teardown(restoreWindow: true, applyFinal: false);
                _wake.WaitOne();
                sw.Restart();
                last = 0;
                continue;
            }

            if (needsSetup)
            {
                lock (_gate) _needsSetup = false;
                Setup();
                sw.Restart();
                last = 0;
                continue;
            }

            double now = sw.Elapsed.TotalSeconds;
            double dt = Math.Min(now - last, 0.03);
            last = now;

            try
            {
                if (_mode == Mode.Mesh) TickMesh(dt);
                else if (_mode == Mode.Rigid) TickRigid(dt);
                else Cancel();
            }
            catch (Exception ex)
            {
                Log.Write("Tick exception: " + ex);
                Teardown(restoreWindow: true, applyFinal: false);
                Cancel();
            }

            Thread.Sleep(Config.TickMs);
        }

        Teardown(restoreWindow: true, applyFinal: false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Setup (engine thread): restore-from-max, measure, capture, go invisible
    // ─────────────────────────────────────────────────────────────────────────
    void Setup()
    {
        // If a previous drag is still settling, finish it cleanly first so its
        // window gets its alpha back and lands on its target.
        if (_mode != Mode.Idle)
            Teardown(restoreWindow: true, applyFinal: _mode == Mode.Mesh);

        IntPtr hwnd; Point grab; bool wasZoomed;
        lock (_gate) { hwnd = _hwnd; grab = _grabCursor; wasZoomed = _wasZoomed; }

        if (!Native.IsWindow(hwnd)) { Cancel(); return; }
        _curHwnd = hwnd;

        // Maximized windows: restore under the cursor (native behavior), then wobble
        if (wasZoomed)
        {
            Native.GetWindowRect(hwnd, out var zrc);
            double relX = Math.Clamp((grab.X - zrc.Left) / (double)Math.Max(1, zrc.Right - zrc.Left), 0.05, 0.95);
            int capOffY = Math.Min(grab.Y - zrc.Top, 28);

            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            Native.GetWindowRect(hwnd, out var rrc);
            int rw = rrc.Right - rrc.Left, rh = rrc.Bottom - rrc.Top;
            Native.SetWindowPos(hwnd, IntPtr.Zero,
                grab.X - (int)(relX * rw), grab.Y - capOffY, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // Visual rect (excludes the invisible resize borders) → what we capture
        Native.GetWindowRect(hwnd, out var wrc);
        var vis = wrc;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var ext, Marshal.SizeOf<Native.RECT>()) == 0
            && ext.Right > ext.Left && ext.Bottom > ext.Top)
        {
            vis = ext;
        }

        _visW = vis.Right - vis.Left;
        _visH = vis.Bottom - vis.Top;
        _visOffX = vis.Left - wrc.Left;
        _visOffY = vis.Top - wrc.Top;
        if (_visW < 8 || _visH < 8) { Cancel(); return; }

        lock (_gate)
        {
            _grabDx = Math.Clamp(_grabCursor.X - vis.Left, 0, _visW);
            _grabDy = Math.Clamp(_grabCursor.Y - vis.Top, 0, _visH);
            _tx = vis.Left;
            _ty = vis.Top;
        }
        _prevTx = vis.Left; _prevTy = vis.Top;
        _lastTx = vis.Left; _lastTy = vis.Top;
        _velEmaX = _velEmaY = 0;
        _tiltX = _tiltY = _tiltVX = _tiltVY = 0;
        _loggedMoveFailure = false;

        ForceForeground(hwnd);

        bool mesh = Settings.Current.DeformEnabled
                    && TryCapture(hwnd, wrc, vis)
                    && TryMakeTransparent(hwnd);

        if (mesh)
        {
            InitMesh();
            RenderOverlay();                       // draw first frame…
            Native.SetWindowPos(_overlay, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            _overlayShown = true;                  // …then reveal, then hide the real one
            Native.SetLayeredWindowAttributes(hwnd, 0, 0, Native.LWA_ALPHA);
            _mode = Mode.Mesh;
            Log.Write($"mesh drag: {_visW}x{_visH}, snapshot scale {_snapScale:0.##}");
        }
        else
        {
            // capture/transparency unavailable → rigid spring-follow fallback
            _rpx = wrc.Left; _rpy = wrc.Top;
            _rvx = _rvy = 0;
            _mode = Mode.Rigid;
            Log.Write("rigid fallback drag (capture or transparency unavailable)");
        }
    }

    bool TryCapture(IntPtr hwnd, Native.RECT wrc, Native.RECT vis)
    {
        try
        {
            int ww = wrc.Right - wrc.Left, wh = wrc.Bottom - wrc.Top;
            using var raw = new Bitmap(ww, wh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(raw))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok) { Log.Write("PrintWindow returned false"); return false; }
            }

            // crop to the visual rect and downscale huge windows
            _snapScale = 1.0;
            double px = (double)_visW * _visH;
            if (px > Config.MaxSnapshotPixels)
                _snapScale = Math.Sqrt(Config.MaxSnapshotPixels / px);

            int sw = Math.Max(8, (int)(_visW * _snapScale));
            int sh = Math.Max(8, (int)(_visH * _snapScale));

            _snapshot?.Dispose();
            _snapshot = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(_snapshot))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.DrawImage(raw,
                    new Rectangle(0, 0, sw, sh),
                    new Rectangle(_visOffX, _visOffY, _visW, _visH),
                    GraphicsUnit.Pixel);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Capture failed: " + ex.Message);
            return false;
        }
    }

    bool TryMakeTransparent(IntPtr hwnd)
    {
        _origExStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((_origExStyle & Native.WS_EX_LAYERED) != 0)
        {
            // already layered (app uses its own alpha) — don't interfere
            Log.Write("window already WS_EX_LAYERED — using rigid mode");
            return false;
        }
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, _origExStyle | Native.WS_EX_LAYERED);
        if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_LAYERED) == 0)
        {
            Log.Write("could not add WS_EX_LAYERED (elevated window?) — using rigid mode");
            return false;
        }
        // a layered window doesn't paint until its attributes are set — keep it
        // fully visible for now; alpha drops to 0 only after the overlay is up
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
        _madeTransparent = true;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mesh physics
    // ─────────────────────────────────────────────────────────────────────────
    void InitMesh()
    {
        var S = Settings.Current;
        double gu, gv, tx, ty;
        lock (_gate) { gu = _grabDx / (double)_visW; gv = _grabDy / (double)_visH; tx = _tx; ty = _ty; }

        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                int i = r * N + c;
                _homeX[i] = _visW * c / (double)(N - 1);
                _homeY[i] = _visH * r / (double)(N - 1);
                _mpx[i] = tx + _homeX[i];
                _mpy[i] = ty + _homeY[i];
                _mvx[i] = 0; _mvy[i] = 0;

                // lead near the grab, trail far from it (unit-square distance
                // so aspect ratio doesn't skew the feel)
                double du = c / (double)(N - 1) - gu;
                double dv = r / (double)(N - 1) - gv;
                double t = Math.Clamp(Math.Sqrt(du * du + dv * dv) / Math.Sqrt(2.0), 0, 1);
                _kHome[i] = S.HomeStiffnessNear +
                            (S.HomeStiffnessFar - S.HomeStiffnessNear) * t;
                _cHome[i] = 2.0 * S.DampingRatio * Math.Sqrt(_kHome[i]);
            }

        // pin the mesh point nearest the grab — zero lag at the hand
        int pr = (int)Math.Round(gv * (N - 1));
        int pc = (int)Math.Round(gu * (N - 1));
        _pin = Math.Clamp(pr, 0, N - 1) * N + Math.Clamp(pc, 0, N - 1);

        // structural springs: grid neighbors + both cell diagonals (shear)
        var springs = new List<(int, int, double)>();
        void AddSpring(int a, int b)
        {
            double dx = _homeX[b] - _homeX[a], dy = _homeY[b] - _homeY[a];
            springs.Add((a, b, Math.Sqrt(dx * dx + dy * dy)));
        }
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                int i = r * N + c;
                if (c + 1 < N) AddSpring(i, i + 1);
                if (r + 1 < N) AddSpring(i, i + N);
                if (c + 1 < N && r + 1 < N) { AddSpring(i, i + N + 1); AddSpring(i + 1, i + N); }
            }
        _springs = springs.ToArray();

        _maxDisp = S.MaxDisplacementFactor * Math.Max(_visW, _visH);
        _structK = S.StructuralStiffness;
    }

    void TickMesh(double dt)
    {
        IntPtr hwnd; double tx, ty; bool dragging, snapCheck; Point releaseCursor;
        lock (_gate)
        {
            if (!_active) return;
            hwnd = _hwnd; tx = _tx; ty = _ty; dragging = _dragging;
            snapCheck = !_dragging && _pendingSnapCheck;
            releaseCursor = _releaseCursor;
            if (snapCheck) _pendingSnapCheck = false;
        }

        if (!Native.IsWindow(hwnd)) { Teardown(restoreWindow: false, applyFinal: false); Cancel(); return; }

        _lastTx = tx; _lastTy = ty;

        if (snapCheck && TrySnapTarget(releaseCursor, out var snapAction))
        {
            // restore the real window, apply the snap, done — no settle needed
            Teardown(restoreWindow: true, applyFinal: false);
            snapAction(hwnd);
            Cancel();
            return;
        }

        // drag velocity (for tilt), exponentially smoothed
        if (dt > 1e-5)
        {
            double ivx = (tx - _prevTx) / dt, ivy = (ty - _prevTy) / dt;
            _velEmaX += (ivx - _velEmaX) * Math.Min(1.0, dt * 14);
            _velEmaY += (ivy - _velEmaY) * Math.Min(1.0, dt * 14);
        }
        _prevTx = tx; _prevTy = ty;

        IntegrateMesh(dt, tx, ty, dragging);
        IntegrateTilt(dt, dragging);

        if (!dragging && IsMeshSettled(tx, ty))
        {
            Teardown(restoreWindow: true, applyFinal: true);
            Cancel();
            return;
        }

        RenderOverlay();
    }

    void IntegrateMesh(double dt, double tx, double ty, bool dragging)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(dt / Config.MaxSubstep));
        double h = dt / steps;
        int pin = dragging ? _pin : -1;

        for (int s = 0; s < steps; s++)
        {
            // pinned point rides the cursor exactly — clean, precise grip
            if (pin >= 0)
            {
                double nx = tx + _homeX[pin], ny = ty + _homeY[pin];
                _mvx[pin] = (nx - _mpx[pin]) / h;
                _mvy[pin] = (ny - _mpy[pin]) / h;
                _mpx[pin] = nx; _mpy[pin] = ny;
            }

            double decay = Math.Max(0.0, 1.0 - Config.GlobalDamping * h);

            for (int i = 0; i < PointCount; i++)
            {
                if (i == pin) continue;

                double fx = _kHome[i] * (tx + _homeX[i] - _mpx[i]) - _cHome[i] * _mvx[i];
                double fy = _kHome[i] * (ty + _homeY[i] - _mpy[i]) - _cHome[i] * _mvy[i];

                _mvx[i] = (_mvx[i] + fx * h) * decay;
                _mvy[i] = (_mvy[i] + fy * h) * decay;
            }

            // structural springs transmit the ripples
            foreach (var (a, b, rest) in _springs)
            {
                double dx = _mpx[b] - _mpx[a];
                double dy = _mpy[b] - _mpy[a];
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-6) continue;
                double f = _structK * (dist - rest) / dist * h;
                if (a != pin) { _mvx[a] += f * dx; _mvy[a] += f * dy; }
                if (b != pin) { _mvx[b] -= f * dx; _mvy[b] -= f * dy; }
            }

            for (int i = 0; i < PointCount; i++)
            {
                if (i == pin) continue;
                _mpx[i] += _mvx[i] * h;
                _mpy[i] += _mvy[i] * h;

                // keep the smear sane
                double ex = _mpx[i] - (tx + _homeX[i]);
                double ey = _mpy[i] - (ty + _homeY[i]);
                double d = Math.Sqrt(ex * ex + ey * ey);
                if (d > _maxDisp)
                {
                    double k = _maxDisp / d;
                    _mpx[i] = tx + _homeX[i] + ex * k;
                    _mpy[i] = ty + _homeY[i] + ey * k;
                }
            }
        }
    }

    void IntegrateTilt(double dt, bool dragging)
    {
        var S = Settings.Current;
        double maxRad = S.TiltMaxDeg * Math.PI / 180.0;
        double targetY = 0, targetX = 0;
        if (S.TiltEnabled && dragging)
        {
            targetY = Math.Clamp(-_velEmaX * Config.TiltPerVelocity * Math.PI / 180.0, -maxRad, maxRad);
            targetX = Math.Clamp(_velEmaY * Config.TiltPerVelocity * Math.PI / 180.0, -maxRad, maxRad);
        }

        double k = S.TiltStiffness;
        double c = 2.0 * Config.TiltDampingRatio * Math.Sqrt(k);

        int steps = Math.Max(1, (int)Math.Ceiling(dt / Config.MaxSubstep));
        double h = dt / steps;
        for (int s = 0; s < steps; s++)
        {
            _tiltVX += (k * (targetX - _tiltX) - c * _tiltVX) * h;
            _tiltVY += (k * (targetY - _tiltY) - c * _tiltVY) * h;
            _tiltX += _tiltVX * h;
            _tiltY += _tiltVY * h;
        }
    }

    bool IsMeshSettled(double tx, double ty)
    {
        if (Math.Abs(_tiltX) > Config.SettleTiltRad || Math.Abs(_tiltY) > Config.SettleTiltRad) return false;
        if (Math.Abs(_tiltVX) > 0.05 || Math.Abs(_tiltVY) > 0.05) return false;
        for (int i = 0; i < PointCount; i++)
        {
            if (Math.Abs(_mpx[i] - (tx + _homeX[i])) >= Config.SettleDistance) return false;
            if (Math.Abs(_mpy[i] - (ty + _homeY[i])) >= Config.SettleDistance) return false;
            if (Math.Abs(_mvx[i]) >= Config.SettleVelocity) return false;
            if (Math.Abs(_mvy[i]) >= Config.SettleVelocity) return false;
        }
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rendering: project mesh through the 3D tilt, warp the snapshot, ULW
    // ─────────────────────────────────────────────────────────────────────────
    void RenderOverlay()
    {
        // centroid for the tilt pivot
        double cx = 0, cy = 0;
        for (int i = 0; i < PointCount; i++) { cx += _mpx[i]; cy += _mpy[i]; }
        cx /= PointCount; cy /= PointCount;

        double sinY = Math.Sin(_tiltY), cosY = Math.Cos(_tiltY);
        double sinX = Math.Sin(_tiltX), cosX = Math.Cos(_tiltX);
        double f = Config.FocalLength;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < PointCount; i++)
        {
            double dx = _mpx[i] - cx, dy = _mpy[i] - cy;
            double z = dx * sinY + dy * sinX;               // depth from combined tilt
            double scale = f / Math.Max(200.0, f + z);      // perspective divide
            float X = (float)(cx + dx * cosY * scale);
            float Y = (float)(cy + dy * cosX * scale);
            _proj[i] = new PointF(X, Y);
            if (X < minX) minX = X; if (X > maxX) maxX = X;
            if (Y < minY) minY = Y; if (Y > maxY) maxY = Y;
        }

        int ox = (int)Math.Floor(minX) - 3, oy = (int)Math.Floor(minY) - 3;
        int bw = (int)Math.Ceiling(maxX) - ox + 6, bh = (int)Math.Ceiling(maxY) - oy + 6;
        bw = Math.Clamp(bw, 8, 4096); bh = Math.Clamp(bh, 8, 4096);

        if (_canvas == null || _canvas.Width < bw || _canvas.Height < bh)
        {
            _canvas?.Dispose();
            _canvas = new Bitmap(Math.Min(4096, bw + bw / 4), Math.Min(4096, bh + bh / 4),
                                 PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_canvas))
        {
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);

            float cellW = _snapshot.Width / (float)(N - 1);
            float cellH = _snapshot.Height / (float)(N - 1);

            // Each cell is drawn as TWO clipped triangles, each with its own
            // exact affine map. A single 3-point DrawImage can only produce a
            // parallelogram; under the 3D perspective the cells become
            // trapezoids, and the parallelogram approximation opens visible
            // gaps along the seams. Triangles represent any warped quad
            // exactly, and a sub-pixel clip overlap hides the shared edges.
            using var clipPath = new GraphicsPath();
            var dest = new PointF[3];
            var tri = new PointF[3];

            for (int r = 0; r < N - 1; r++)
                for (int c = 0; c < N - 1; c++)
                {
                    int i = r * N + c;
                    var dUL = new PointF(_proj[i].X - ox, _proj[i].Y - oy);
                    var dUR = new PointF(_proj[i + 1].X - ox, _proj[i + 1].Y - oy);
                    var dLL = new PointF(_proj[i + N].X - ox, _proj[i + N].Y - oy);
                    var dBR = new PointF(_proj[i + N + 1].X - ox, _proj[i + N + 1].Y - oy);

                    float sx = c * cellW, sy = r * cellH;
                    float sw = Math.Min(cellW, _snapshot.Width - sx);
                    float sh = Math.Min(cellH, _snapshot.Height - sy);
                    var src = new RectangleF(sx, sy, sw, sh);

                    // triangle 1: UL-UR-LL — its affine map IS the 3-point form
                    dest[0] = dUL; dest[1] = dUR; dest[2] = dLL;
                    tri[0] = dUL; tri[1] = dUR; tri[2] = dLL;
                    DrawTriangle(g, clipPath, _snapshot, dest, tri, src);

                    // triangle 2: UR-BR-LL — same 3-point form, but with the
                    // implied fourth corner UL' = UR + LL - BR so that the
                    // affine map sends src UR→dUR, src BR→dBR, src LL→dLL
                    dest[0] = new PointF(dUR.X + dLL.X - dBR.X, dUR.Y + dLL.Y - dBR.Y);
                    dest[1] = dUR; dest[2] = dLL;
                    tri[0] = dUR; tri[1] = dBR; tri[2] = dLL;
                    DrawTriangle(g, clipPath, _snapshot, dest, tri, src);
                }

            g.ResetClip();
        }

        PushToOverlay(_canvas, ox, oy, bw, bh);
    }

    static void DrawTriangle(Graphics g, GraphicsPath clipPath, Bitmap snapshot,
        PointF[] destParallelogram, PointF[] clipTri, RectangleF src)
    {
        // expand the clip triangle ~0.8px outward from its centroid so
        // neighboring triangles overlap by a hair — no cracks, and the two
        // affine maps agree exactly on the shared edge so the overlap is
        // invisible
        float cx = (clipTri[0].X + clipTri[1].X + clipTri[2].X) / 3f;
        float cy = (clipTri[0].Y + clipTri[1].Y + clipTri[2].Y) / 3f;
        Span<PointF> inflated = stackalloc PointF[3];
        for (int k = 0; k < 3; k++)
        {
            float dx = clipTri[k].X - cx, dy = clipTri[k].Y - cy;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            float s = len > 0.001f ? (len + 0.8f) / len : 1f;
            inflated[k] = new PointF(cx + dx * s, cy + dy * s);
        }

        clipPath.Reset();
        clipPath.AddPolygon(new[] { inflated[0], inflated[1], inflated[2] });
        g.SetClip(clipPath);
        g.DrawImage(snapshot, destParallelogram, src, GraphicsUnit.Pixel);
    }

    void PushToOverlay(Bitmap bmp, int x, int y, int w, int h)
    {
        IntPtr screenDC = Native.GetDC(IntPtr.Zero);
        IntPtr memDC = Native.CreateCompatibleDC(screenDC);
        IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = Native.SelectObject(memDC, hBmp);

            var dst = new Native.POINT { X = x, Y = y };
            var size = new Native.SIZE { cx = w, cy = h };
            var src = new Native.POINT { X = 0, Y = 0 };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = 0,                 // AC_SRC_OVER
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,             // AC_SRC_ALPHA
            };
            Native.UpdateLayeredWindow(_overlay, screenDC, ref dst, ref size,
                memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDC, old);
            if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
            Native.DeleteDC(memDC);
            Native.ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rigid fallback: spring-follow with overshoot, position-only moves
    // ─────────────────────────────────────────────────────────────────────────
    void TickRigid(double dt)
    {
        IntPtr hwnd; double tx, ty; bool dragging, snapCheck; Point releaseCursor;
        lock (_gate)
        {
            if (!_active) return;
            hwnd = _hwnd; dragging = _dragging;
            tx = _tx - _visOffX; ty = _ty - _visOffY;   // visual → window rect
            snapCheck = !_dragging && _pendingSnapCheck;
            releaseCursor = _releaseCursor;
            if (snapCheck) _pendingSnapCheck = false;
        }

        if (!Native.IsWindow(hwnd)) { Cancel(); return; }

        if (snapCheck && TrySnapTarget(releaseCursor, out var snapAction))
        {
            snapAction(hwnd);
            Cancel();
            return;
        }

        double k = Settings.Current.RigidStiffness;
        double c = 2.0 * 0.45 * Math.Sqrt(k);
        int steps = Math.Max(1, (int)Math.Ceiling(dt / Config.MaxSubstep));
        double h = dt / steps;
        for (int s = 0; s < steps; s++)
        {
            _rvx += (k * (tx - _rpx) - c * _rvx) * h;
            _rvy += (k * (ty - _rpy) - c * _rvy) * h;
            _rpx += _rvx * h;
            _rpy += _rvy * h;
        }

        bool settled = !dragging
            && Math.Abs(_rvx) < Config.SettleVelocity && Math.Abs(_rvy) < Config.SettleVelocity
            && Math.Abs(tx - _rpx) < Config.SettleDistance && Math.Abs(ty - _rpy) < Config.SettleDistance;

        int X = (int)Math.Round(settled ? tx : _rpx);
        int Y = (int)Math.Round(settled ? ty : _rpy);

        bool ok = Native.SetWindowPos(hwnd, IntPtr.Zero, X, Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE |
            Native.SWP_NOOWNERZORDER | Native.SWP_ASYNCWINDOWPOS);

        if (!ok && !_loggedMoveFailure)
        {
            _loggedMoveFailure = true;
            Log.Write($"SetWindowPos FAILED, Win32 error {Marshal.GetLastWin32Error()} " +
                      "(elevated window? run WobblyWindows as admin)");
        }

        if (settled) { _mode = Mode.Idle; Cancel(); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Finish / abort: put the real window back, exactly once
    // ─────────────────────────────────────────────────────────────────────────
    void Teardown(bool restoreWindow, bool applyFinal)
    {
        IntPtr hwnd = _curHwnd;
        double tx = _lastTx, ty = _lastTy;

        try
        {
            if (_mode == Mode.Mesh && restoreWindow && Native.IsWindow(hwnd))
            {
                if (applyFinal)
                {
                    Native.SetWindowPos(hwnd, IntPtr.Zero,
                        (int)Math.Round(tx) - _visOffX, (int)Math.Round(ty) - _visOffY, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE |
                        Native.SWP_NOOWNERZORDER);
                }
                if (_madeTransparent)
                {
                    Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
                    Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, _origExStyle);
                }
            }
        }
        catch (Exception ex) { Log.Write("Teardown: " + ex.Message); }

        _madeTransparent = false;

        if (_overlayShown)
        {
            Native.ShowWindow(_overlay, Native.SW_HIDE);
            _overlayShown = false;
        }
        _snapshot?.Dispose(); _snapshot = null;
        _mode = Mode.Idle;
        _pin = -1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Edge snap on release: top = maximize, left/right = half snap
    // ─────────────────────────────────────────────────────────────────────────
    static bool TrySnapTarget(Point cursor, out Action<IntPtr> apply)
    {
        apply = null;
        IntPtr mon = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MONITORINFO.New();
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;

        var m = mi.rcMonitor;
        var wa = mi.rcWork;

        if (cursor.Y <= m.Top + Config.SnapThreshold)
        {
            apply = h => Native.ShowWindow(h, Native.SW_MAXIMIZE);
            return true;
        }
        if (cursor.X <= m.Left + Config.SnapThreshold)
        {
            apply = h => Native.SetWindowPos(h, IntPtr.Zero, wa.Left, wa.Top,
                (wa.Right - wa.Left) / 2, wa.Bottom - wa.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return true;
        }
        if (cursor.X >= m.Right - 1 - Config.SnapThreshold)
        {
            int half = (wa.Right - wa.Left) / 2;
            apply = h => Native.SetWindowPos(h, IntPtr.Zero, wa.Left + half, wa.Top,
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
        _thread.Join(500);
        _canvas?.Dispose();
        _snapshot?.Dispose();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Settings window — dark theme, sliders save instantly
// ─────────────────────────────────────────────────────────────────────────────
sealed class SettingsForm : Form
{
    static SettingsForm _open;
    public static void ShowSingleton()
    {
        if (_open != null && !_open.IsDisposed)
        {
            _open.Activate();
            _open.BringToFront();
            return;
        }
        _open = new SettingsForm();
        _open.Show();
    }

    static readonly Color Bg = Color.FromArgb(10, 10, 26);
    static readonly Color Accent = Color.FromArgb(0, 229, 255);
    static readonly Color Dim = Color.FromArgb(120, 130, 170);
    static readonly Color Txt = Color.FromArgb(222, 230, 255);

    readonly List<Action> _refreshers = new();
    static Settings S => Settings.Current;

    public SettingsForm()
    {
        Text = "WobblyWindows — Settings";
        BackColor = Bg;
        ForeColor = Txt;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;

        int y = 14;

        Header("MOTION", ref y);
        AddSlider("Speed", " %", 40, 250,
            () => S.SpeedPercent, v => S.SpeedPercent = v, ref y,
            "How fast the jelly reacts and settles");
        AddSlider("Wobble amount", "", 0, 100,
            () => S.WobbleAmount, v => S.WobbleAmount = v, ref y,
            "How many rebounds after release (0 = none)");

        Header("JELLY", ref y);
        AddSlider("Softness", "", 0, 100,
            () => S.JellySoftness, v => S.JellySoftness = v, ref y,
            "How rubbery the trailing stretch is");
        AddSlider("Max stretch", " %", 20, 90,
            () => S.MaxStretchPercent, v => S.MaxStretchPercent = v, ref y,
            "Cap on how far the surface can smear");
        AddCheck("Jelly deformation (mesh warp)",
            () => S.DeformEnabled, v => S.DeformEnabled = v, ref y);

        Header("3D TILT", ref y);
        AddSlider("Tilt angle", "°", 0, 30,
            () => S.TiltMaxDeg, v => S.TiltMaxDeg = v, ref y,
            "How far the window leans into the motion");
        AddCheck("3D tilt enabled",
            () => S.TiltEnabled, v => S.TiltEnabled = v, ref y);

        Header("BEHAVIOR", ref y);
        AddCheck("Edge snap on release (top = maximize, sides = half)",
            () => S.SnapEnabled, v => S.SnapEnabled = v, ref y);
        AddCheck("Show welcome popup at startup",
            () => S.ShowWelcomeAtStartup, v => S.ShowWelcomeAtStartup = v, ref y);

        y += 8;
        var note = new Label
        {
            Text = "Changes save automatically and apply to the next drag.",
            ForeColor = Dim,
            AutoSize = false,
            Location = new Point(16, y),
            Size = new Size(300, 34),
        };
        Controls.Add(note);

        var reset = new Button
        {
            Text = "Reset defaults",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Accent,
            BackColor = Color.FromArgb(16, 18, 40),
            Location = new Point(318, y - 2),
            Size = new Size(110, 30),
        };
        reset.FlatAppearance.BorderColor = Accent;
        reset.Click += (_, _) =>
        {
            S.ResetToDefaults();
            Settings.Save();
            foreach (var r in _refreshers) r();
        };
        Controls.Add(reset);

        y += 44;
        ClientSize = new Size(444, y);
        FormClosed += (_, _) => { if (_open == this) _open = null; };
    }

    void Header(string text, ref int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            ForeColor = Accent,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Location = new Point(16, y),
            AutoSize = true,
        });
        y += 22;
    }

    void AddSlider(string label, string unit, int min, int max,
        Func<int> get, Action<int> set, ref int y, string hint)
    {
        var name = new Label { Text = label, Location = new Point(24, y), AutoSize = true, ForeColor = Txt };
        var val = new Label
        {
            Text = get() + unit,
            Location = new Point(360, y),
            Size = new Size(68, 16),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Accent,
        };
        var bar = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(get(), min, max),
            TickStyle = TickStyle.None,
            Location = new Point(20, y + 18),
            Size = new Size(408, 30),
            BackColor = Bg,
        };
        var tip = new ToolTip();
        tip.SetToolTip(bar, hint);
        bar.ValueChanged += (_, _) =>
        {
            set(bar.Value);
            val.Text = bar.Value + unit;
            Settings.Save();
        };
        _refreshers.Add(() =>
        {
            bar.Value = Math.Clamp(get(), min, max);
            val.Text = bar.Value + unit;
        });
        Controls.Add(name);
        Controls.Add(val);
        Controls.Add(bar);
        y += 52;
    }

    void AddCheck(string label, Func<bool> get, Action<bool> set, ref int y)
    {
        var cb = new CheckBox
        {
            Text = label,
            Checked = get(),
            Location = new Point(24, y),
            AutoSize = true,
            ForeColor = Txt,
        };
        cb.CheckedChanged += (_, _) =>
        {
            set(cb.Checked);
            Settings.Save();
        };
        _refreshers.Add(() => cb.Checked = get());
        Controls.Add(cb);
        y += 28;
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
    public const int SW_HIDE = 0;
    public const int SW_MAXIMIZE = 3;
    public const int SW_RESTORE = 9;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const uint LWA_ALPHA = 0x2;
    public const uint PW_RENDERFULLCONTENT = 0x2;
    public const uint ULW_ALPHA = 0x2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

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
