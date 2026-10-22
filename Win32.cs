using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinAutomationMcp;

/// <summary>Thin P/Invoke layer. Everything here works in physical pixels because Program sets per-monitor DPI awareness.</summary>
static class Native
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref uint size);
    [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_POPUP = 0x80000000L, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public static readonly IntPtr DPI_PER_MONITOR_V2 = new IntPtr(-4);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() => new Rectangle(Left, Top, Right - Left, Bottom - Top);
    }

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public static Rectangle VirtualScreen() =>
        new Rectangle(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    // INPUT is a union of MOUSEINPUT/KEYBDINPUT/HARDWAREINPUT. The keyboard member is the largest on 64-bit
    // ARM/x64 only by padding, so declare the union explicitly to get the right native size.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    public static void MouseButton(bool down, string button)
    {
        uint flag = button.ToLowerInvariant() switch
        {
            "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
        };
        var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void MouseMoveAbsolute(int x, int y)
    {
        var v = VirtualScreen();
        int normX = (int)(((long)(x - v.Left) * 65536L) / Math.Max(1, v.Width));
        int normY = (int)(((long)(y - v.Top) * 65536L) / Math.Max(1, v.Height));
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dx = normX, dy = normY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ---- window helpers ----

    public static Rectangle OuterRect(IntPtr h) => GetWindowRect(h, out var r) ? r.ToRectangle() : Rectangle.Empty;

    /// <summary>Visible frame (without the invisible resize border / shadow). Falls back to GetWindowRect.</summary>
    public static Rectangle VisibleRect(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0)
        {
            var rr = r.ToRectangle();
            if (rr.Width > 0 && rr.Height > 0) return rr;
        }
        return OuterRect(h);
    }

    public static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int v, 4) == 0 && v != 0;
    public static uint PidOf(IntPtr h) { GetWindowThreadProcessId(h, out var pid); return pid; }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }

    public static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Bring a window to the foreground (restoring it if minimized). Returns true if the window (or a window of the same process) ends up in front.</summary>
    public static bool Focus(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        if (IsIconic(h)) { ShowWindow(h, SW_RESTORE); Thread.Sleep(150); }
        if (SameProcessForeground(h)) return true;
        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint cur = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true);
        try { BringWindowToTop(h); SetForegroundWindow(h); }
        finally { if (attached) AttachThreadInput(cur, fgThread, false); }
        for (int i = 0; i < 10; i++) { if (SameProcessForeground(h)) return true; Thread.Sleep(40); }
        return false;
    }

    private static bool SameProcessForeground(IntPtr h)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == h) return true;
        return PidOf(fg) == PidOf(h);
    }

    /// <summary>Visible top-level popup-like windows of the same process that sit near the main window (combo/dropdown lists, menus, tooltips).</summary>
    public static List<Rectangle> PopupRects(IntPtr main, int margin = 300)
    {
        var res = new List<Rectangle>();
        uint pid = PidOf(main);
        var mainRect = VisibleRect(main);
        var near = Rectangle.Inflate(mainRect, margin, margin);
        var vs = VirtualScreen();
        EnumWindows((h, _) =>
        {
            if (h == main || !IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            if (PidOf(h) != pid) return true;
            long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            bool owned = GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero;
            bool popupLike = (style & WS_POPUP) != 0 || (ex & WS_EX_TOOLWINDOW) != 0 || owned;
            if (!popupLike) return true;
            var r = VisibleRect(h);
            if (r.Width < 4 || r.Height < 4) return true;
            if (r.Width >= vs.Width && r.Height >= vs.Height) return true; // full-screen overlay, not a popup
            if (!r.IntersectsWith(near)) return true;
            res.Add(r);
            return true;
        }, IntPtr.Zero);
        return res;
    }
}
