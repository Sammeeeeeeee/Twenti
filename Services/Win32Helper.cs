using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Twenti.Services;

internal static class Win32Helper
{
    public static double GetDpiScale(IntPtr hwnd)
    {
        int dpi = GetDpiForWindow(hwnd);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// DPI scale factor for the monitor that contains the given DisplayArea.
    /// Use this instead of the window's current DPI when sizing a window
    /// that's about to be MoveAndResize'd onto a different monitor — different
    /// monitors can have different DPI, and using the source monitor's scale
    /// produces visibly clipped content on the target.
    /// </summary>
    public static double GetDpiScaleForDisplayArea(DisplayArea? area)
    {
        if (area is null) return 1.0;
        var bounds = area.OuterBounds;
        var pt = new POINT
        {
            X = bounds.X + bounds.Width / 2,
            Y = bounds.Y + bounds.Height / 2,
        };
        IntPtr hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero) return 1.0;
        if (GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) != 0) return 1.0;
        return dpiX <= 0 ? 1.0 : dpiX / 96.0;
    }

    public static PointInt32 GetCursorPos()
    {
        if (NativeGetCursorPos(out var p))
        {
            return new PointInt32(p.X, p.Y);
        }
        return new PointInt32(0, 0);
    }

    public static DisplayArea GetCursorDisplayArea()
    {
        var pt = GetCursorPos();
        return DisplayArea.GetFromPoint(pt, DisplayAreaFallback.Nearest);
    }

    public static void RoundCorners(IntPtr hwnd, bool small = false)
    {
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33
        // DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3
        int pref = small ? 3 : 2;
        DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
    }

    public static void RemoveBorder(IntPtr hwnd)
    {
        // DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = 0xFFFFFFFE
        int color = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34, ref color, sizeof(int));
    }

    public static void ForceImmersiveDark(IntPtr hwnd)
    {
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 — keeps the system-drawn chrome dark.
        int value = 1;
        DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
    }

    /// <summary>
    /// Switches the window to a true popup style (WS_POPUP), stripping every system-drawn
    /// chrome element: caption, sizing border, dialog frame. WinAppSDK's
    /// OverlappedPresenter.SetBorderAndTitleBar(false, false) only hides them visually;
    /// this removes the styles entirely so DWM has nothing to draw at the edge.
    /// </summary>
    public static void MakeBorderless(IntPtr hwnd)
    {
        const int GWL_STYLE = -16;
        const long WS_CAPTION       = 0x00C00000L;
        const long WS_THICKFRAME    = 0x00040000L;
        const long WS_MINIMIZEBOX   = 0x00020000L;
        const long WS_MAXIMIZEBOX   = 0x00010000L;
        const long WS_SYSMENU       = 0x00080000L;
        const long WS_DLGFRAME      = 0x00400000L;
        const long WS_BORDER        = 0x00800000L;
        const long WS_POPUP         = unchecked((long)0x80000000L);

        const long stripMask = WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX
                             | WS_MAXIMIZEBOX | WS_SYSMENU | WS_DLGFRAME | WS_BORDER;

        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~stripMask) | WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        // Tell the OS to recompute the non-client area using the new styles.
        const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004,
                   SWP_FRAMECHANGED = 0x0020;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    public static void HideFromAltTab(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>
    /// Force a window to the foreground even when Windows' foreground-lock
    /// rules would normally refuse. The popup is shown from a timer tick
    /// (no recent user input on our process), so a plain SetForegroundWindow
    /// gets demoted to a taskbar flash and the window opens behind whatever
    /// the user is actually working on — keystrokes (Enter / 1-9 / Esc) then
    /// go to the wrong window.
    ///
    /// The classic workaround: temporarily attach our input queue to the
    /// foreground thread's, which makes the OS treat us as having the same
    /// input attention. Activate during the attach window, then detach.
    /// </summary>
    public static void ForceForegroundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        IntPtr fgHwnd = GetForegroundWindow();
        uint thisThread = GetCurrentThreadId();
        uint targetThread = fgHwnd == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fgHwnd, IntPtr.Zero);
        bool attached = false;

        try
        {
            if (targetThread != 0 && targetThread != thisThread)
            {
                attached = AttachThreadInput(targetThread, thisThread, true);
            }

            // ShowWindow first in case the window is minimised (popup won't
            // be, but defensive); BringWindowToTop reorders Z; then the real
            // SetForegroundWindow under the borrowed input attention.
            ShowWindow(hwnd, SW_SHOW);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Best-effort — failure here just means the window opens behind,
            // which is exactly the state we were trying to avoid.
        }
        finally
        {
            if (attached)
            {
                try { AttachThreadInput(targetThread, thisThread, false); }
                catch { /* swallow */ }
            }
        }
    }

    // ── Global foreground-change hook ──────────────────────────────────────
    public delegate void WinEventCallback(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT  = 0x0000;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventCallback lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool NativeGetCursorPos(out POINT lpPoint);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
