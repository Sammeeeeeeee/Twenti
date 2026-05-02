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
}
