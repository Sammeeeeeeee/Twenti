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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

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
}
