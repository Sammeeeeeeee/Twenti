using System;
using Windows.UI.ViewManagement;

namespace Twenti.Services;

public sealed class ThemeListener
{
    private readonly UISettings _settings = new();
    private bool _isDark;

    public bool IsDark => _isDark;

    public event EventHandler? ThemeChanged;

    public ThemeListener()
    {
        _isDark = ComputeIsDark(_settings);
        _settings.ColorValuesChanged += (_, _) =>
        {
            var newDark = ComputeIsDark(_settings);
            if (newDark != _isDark)
            {
                _isDark = newDark;
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>
    /// Probe the system theme without constructing a listener. Used during
    /// the synchronous Phase-1 startup where we need a colour decision
    /// before the listener exists.
    /// </summary>
    public static bool ComputeIsDarkStatic()
    {
        try
        {
            return ComputeIsDark(new UISettings());
        }
        catch
        {
            return true; // dark is the more common Windows 11 default
        }
    }

    private static bool ComputeIsDark(UISettings settings)
    {
        var bg = settings.GetColorValue(UIColorType.Background);
        return bg.R + bg.G + bg.B < 384;
    }
}
