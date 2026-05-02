using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Twenti.Services;

public sealed class TrayIconRenderer
{
    private const int IconSize = 32;

    public Icon Render(BreakStateMachine sm, bool darkTheme)
    {
        using var bmp = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            DrawState(g, sm, darkTheme);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(h).Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    private static void DrawState(Graphics g, BreakStateMachine sm, bool darkTheme)
    {
        Color fg;
        string label;
        bool drawEye = false;

        switch (sm.Phase)
        {
            case Phase.Snoozed:
                {
                    int total = sm.SnoozeLeftSec;
                    label = total >= 60 ? $"{total / 60}m" : $"{total}s";
                    fg = darkTheme
                        ? Color.FromArgb(204, 255, 255, 255)
                        : Color.FromArgb(160, 0, 0, 0);
                    break;
                }
            case Phase.Alert:
            case Phase.Break:
                drawEye = true;
                label = string.Empty;
                fg = darkTheme ? Color.FromArgb(255, 96, 205, 255) : Color.FromArgb(255, 0, 103, 192);
                break;
            case Phase.PrePing:
                label = $"{sm.WorkLeftSec}s";
                fg = darkTheme ? Color.FromArgb(255, 252, 225, 0) : Color.FromArgb(255, 131, 91, 0);
                break;
            default:
                {
                    int left = sm.WorkLeftSec;
                    bool yellow = left <= 120;
                    label = left >= 60 ? $"{left / 60}" : $"{left}s";
                    fg = yellow
                        ? (darkTheme ? Color.FromArgb(255, 252, 225, 0) : Color.FromArgb(255, 131, 91, 0))
                        : (darkTheme ? Color.FromArgb(255, 108, 203, 95) : Color.FromArgb(255, 15, 123, 15));
                    break;
                }
        }

        if (drawEye)
        {
            DrawEye(g, fg);
            return;
        }

        DrawNumber(g, label, fg);
    }

    private static void DrawNumber(Graphics g, string label, Color fg)
    {
        float fontSize = label.Length switch
        {
            <= 1 => 22f,
            2 => 19f,
            3 => 14f,
            _ => 11f,
        };

        using var font = new Font("Segoe UI Variable Display", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(fg);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var rect = new RectangleF(0, 0, IconSize, IconSize);
        g.DrawString(label, font, brush, rect, fmt);
    }

    private static void DrawEye(Graphics g, Color fg)
    {
        const int cx = IconSize / 2;
        const int cy = IconSize / 2;

        using var pen = new Pen(fg, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(fg);

        var outline = new RectangleF(cx - 12, cy - 7, 24, 14);
        using var path = new GraphicsPath();
        path.AddBezier(outline.Left, cy, outline.Left + 4, outline.Top, outline.Right - 4, outline.Top, outline.Right, cy);
        path.AddBezier(outline.Right, cy, outline.Right - 4, outline.Bottom, outline.Left + 4, outline.Bottom, outline.Left, cy);
        g.DrawPath(pen, path);

        g.FillEllipse(fill, cx - 4, cy - 4, 8, 8);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
