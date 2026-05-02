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
        _isDark = ComputeIsDark();
        _settings.ColorValuesChanged += (_, _) =>
        {
            var newDark = ComputeIsDark();
            if (newDark != _isDark)
            {
                _isDark = newDark;
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private bool ComputeIsDark()
    {
        var bg = _settings.GetColorValue(UIColorType.Background);
        return bg.R + bg.G + bg.B < 384;
    }
}
