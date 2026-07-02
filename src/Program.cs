// WobblyWindows — Compiz-style wobbly window dragging for Windows 11
// Works on real app windows (Chrome, Discord, Explorer, etc.)
//
// How it works:
//   1. A low-level mouse hook watches for a left-click that lands on a
//      window's caption / drag region (WM_NCHITTEST == HTCAPTION).
//   2. The native drag is swallowed and WobblyWindows takes over the move,
//      driving the window with an under-damped spring so it lags, overshoots
//      and jiggles like jelly. Optional squash & stretch deforms the window
//      size based on velocity.
//   3. On release the window keeps oscillating until it settles, then the
//      exact target position/size is restored. Basic Aero-snap (top =
//      maximize, left/right edge = half snap) is emulated.

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WobblyWindows;

static class Program
{
    [STAThread]
    static void Main()
    {
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var tray = new TrayApp();
        Application.Run();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tunables — tweak to taste
// ─────────────────────────────────────────────────────────────────────────────
static class Config
{
    // Spring stiffness (higher = snappier, lower = floatier)
    public const double Stiffness = 620.0;
    // Damping ratio (1.0 = no wobble, 0.2 = very jiggly)
    public const double DampingRatio = 0.30;
    // Squash & stretch amount per px/s of velocity
    public const double SquashPerVelocity = 0.00028;
    // Max squash/stretch (0.15 = up to 15% deformation)
    public const double SquashMax = 0.14;
    // Physics tick target (ms)
    public const int TickMs = 4;
    // Snap threshold in px from monitor edge
    public const int SnapThreshold = 6;
    // Settle thresholds
    public const double SettleVelocity = 25.0;   // px/s
    public const double SettleDistance = 0.8;    // px
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
        enabled.CheckedChanged += (_, _) => _hook.Enabled = enabled.Checked;

        var squash = new ToolStripMenuItem("Squash && stretch") { Checked = _engine.SquashEnabled, CheckOnClick = true };
        squash.CheckedChanged += (_, _) => _engine.SquashEnabled = squash.Checked;

        var snap = new ToolStripMenuItem("Edge snap on release") { Checked = _engine.SnapEnabled, CheckOnClick = true };
        snap.CheckedChanged += (_, _) => _engine.SnapEnabled = snap.Checked;

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { Dispose(); Application.Exit(); };

        menu.Items.Add(enabled);
        menu.Items.Add(squash);
        menu.Items.Add(snap);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "WobblyWindows — drag any title bar",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _hook.Install();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(255, 0, 229, 255));
            using var bar = new SolidBrush(Color.FromArgb(255, 10, 10, 26));
            // wobbly window blob
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
    };

    public MouseHook(WobbleEngine engine)
    {
        _engine = engine;
        _proc = HookProc;
    }

    public void Install()
    {
        _hookHandle = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc,
            Native.GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
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
        return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    bool OnButtonDown(Point pt)
    {
        IntPtr hit = Native.WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return false;

        IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (root == IntPtr.Zero || !Native.IsWindowVisible(root)) return false;
        if (Native.IsZoomed(root) || Native.IsIconic(root)) return false; // let native handle maximized

        Native.GetWindowThreadProcessId(root, out uint pid);
        if (pid == _ownPid) return false;

        string cls = Native.GetClassNameSafe(root);
        foreach (var ex in ExcludedClasses)
            if (string.Equals(cls, ex, StringComparison.OrdinalIgnoreCase)) return false;

        // Ask the window what's under the cursor — HTCAPTION covers native
        // title bars *and* custom drag regions (Chrome tab strip, Discord's
        // Electron drag area, etc.)
        if (!Native.TryHitTest(root, pt, out int hitCode) || hitCode != Native.HTCAPTION)
            return false;

        // Double-click on caption → forward as maximize toggle instead of drag
        long now = Environment.TickCount64;
        if (root == _lastDownWindow
            && now - _lastDownTick <= Native.GetDoubleClickTime()
            && Math.Abs(pt.X - _lastDownPoint.X) <= SystemInformation.DoubleClickSize.Width
            && Math.Abs(pt.Y - _lastDownPoint.Y) <= SystemInformation.DoubleClickSize.Height)
        {
            _lastDownTick = 0;
            _engine.Cancel();
            Native.PostMessage(root, Native.WM_NCLBUTTONDBLCLK,
                (IntPtr)Native.HTCAPTION, Native.MakeLParam(pt.X, pt.Y));
            return true;
        }

        _lastDownTick = now;
        _lastDownWindow = root;
        _lastDownPoint = pt;

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
// Physics: under-damped spring drives the window toward the cursor
// ─────────────────────────────────────────────────────────────────────────────
sealed class WobbleEngine : IDisposable
{
    public volatile bool SquashEnabled = true;
    public volatile bool SnapEnabled = true;
    public bool Dragging { get { lock (_gate) return _dragging; } }

    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _shutdown;

    // state (guarded by _gate)
    bool _dragging, _active;
    IntPtr _hwnd;
    double _px, _py, _vx, _vy;      // simulated position / velocity (top-left)
    double _tx, _ty;                // target position (cursor - grab offset)
    int _grabDx, _grabDy;
    int _baseW, _baseH;
    Point _releaseCursor;
    bool _pendingSnapCheck;

    public WobbleEngine()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "WobblePhysics" };
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    public void BeginDrag(IntPtr hwnd, Point cursor)
    {
        if (!Native.GetWindowRect(hwnd, out var rc)) return;

        lock (_gate)
        {
            _hwnd = hwnd;
            _px = rc.Left; _py = rc.Top;
            _vx = _vy = 0;
            _grabDx = cursor.X - rc.Left;
            _grabDy = cursor.Y - rc.Top;
            _tx = _px; _ty = _py;
            _baseW = rc.Right - rc.Left;
            _baseH = rc.Bottom - rc.Top;
            _dragging = true;
            _active = true;
            _pendingSnapCheck = false;
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

            Step(dt);
            Thread.Sleep(Config.TickMs);
        }
    }

    void Step(double dt)
    {
        IntPtr hwnd;
        double px, py, vx, vy, tx, ty;
        bool dragging, snapCheck;
        int baseW, baseH;
        Point releaseCursor;

        lock (_gate)
        {
            if (!_active) return;
            hwnd = _hwnd;
            px = _px; py = _py; vx = _vx; vy = _vy; tx = _tx; ty = _ty;
            dragging = _dragging;
            baseW = _baseW; baseH = _baseH;
            snapCheck = _pendingSnapCheck;
            releaseCursor = _releaseCursor;
        }

        if (!Native.IsWindow(hwnd)) { Cancel(); return; }

        // Snap check fires once, right after release
        if (!dragging && snapCheck)
        {
            lock (_gate) _pendingSnapCheck = false;
            if (TryEdgeSnap(hwnd, releaseCursor)) { Cancel(); return; }
        }

        // Semi-implicit Euler spring integration
        double w = Math.Sqrt(Config.Stiffness);
        double c = 2.0 * Config.DampingRatio * w;

        double ax = Config.Stiffness * (tx - px) - c * vx;
        double ay = Config.Stiffness * (ty - py) - c * vy;
        vx += ax * dt; vy += ay * dt;
        px += vx * dt; py += vy * dt;

        // Squash & stretch based on velocity
        int outW = baseW, outH = baseH;
        double drawX = px, drawY = py;
        uint sizeFlag = Native.SWP_NOSIZE;
        if (SquashEnabled)
        {
            sizeFlag = 0;
            double sx = Math.Min(Math.Abs(vx) * Config.SquashPerVelocity, Config.SquashMax);
            double sy = Math.Min(Math.Abs(vy) * Config.SquashPerVelocity, Config.SquashMax);
            outW = (int)Math.Round(baseW * (1.0 + sx - sy * 0.5));
            outH = (int)Math.Round(baseH * (1.0 + sy - sx * 0.5));
            drawX = px - (outW - baseW) / 2.0;
            drawY = py - (outH - baseH) / 2.0;
        }

        bool settled = !dragging
            && Math.Abs(vx) < Config.SettleVelocity && Math.Abs(vy) < Config.SettleVelocity
            && Math.Abs(tx - px) < Config.SettleDistance && Math.Abs(ty - py) < Config.SettleDistance;

        if (settled)
        {
            // land exactly on target with original size
            Native.SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(tx), (int)Math.Round(ty), baseW, baseH,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            Cancel();
            return;
        }

        Native.SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(drawX), (int)Math.Round(drawY), outW, outH,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER |
            Native.SWP_ASYNCWINDOWPOS | sizeFlag);

        lock (_gate)
        {
            if (!_active) return;
            _px = px; _py = py; _vx = vx; _vy = vy;
        }
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
    public struct POINT { public int X, Y; }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

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
    public static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    public static string GetClassNameSafe(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool TryHitTest(IntPtr hwnd, Point pt, uint timeoutMs, out int hitCode)
    {
        hitCode = 0;
        IntPtr ok = SendMessageTimeout(hwnd, WM_NCHITTEST, IntPtr.Zero,
            MakeLParam(pt.X, pt.Y), SMTO_ABORTIFHUNG, timeoutMs, out IntPtr result);
        if (ok == IntPtr.Zero) return false;
        hitCode = (int)(long)result;
        return true;
    }

    public static bool TryHitTest(IntPtr hwnd, Point pt, out int hitCode)
        => TryHitTest(hwnd, pt, 30, out hitCode);
}
