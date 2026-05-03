using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Twenti.Services;

/// <summary>
/// Manages the per-user "Start with Windows" registry entry.
/// Writes to HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Twenti.
/// Per-user, no admin required, persists across reboots.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Twenti";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) return;
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // ignore — non-critical
        }
    }
}
