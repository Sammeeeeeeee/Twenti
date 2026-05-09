using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Twenti.Services;

/// <summary>
/// Minimal append-only file logger. One line per entry, rotated at
/// MaxBytes by truncate-and-continue (keeps the most recent traffic; old
/// entries are dropped — fine for a tray app where we mostly care about
/// the last crash).
/// </summary>
public static class Logger
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Twenti", "logs", "twenti.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    public static void Error(string message, Exception ex)
    {
        Write("ERR ", $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    /// <summary>
    /// Lightweight breadcrumb — call this at the entry/exit of any user
    /// action. Idea: when the process FailFasts (e.g. H.NotifyIcon's
    /// GC'd-delegate bug), every catch block is bypassed but the most
    /// recent breadcrumb has already been flushed to disk, so we can
    /// see what the user was doing right before the crash.
    /// </summary>
    public static void Breadcrumb(string action) => Write("CRMB", action);

    private static int _processExitInstalled;

    /// <summary>
    /// Subscribe to ProcessExit so the log shows clean shutdowns. We do
    /// NOT hook FirstChanceException — WinRT/WinUI throw a constant stream
    /// of caught-internally exceptions during click/menu/render paths, and
    /// serialising every one of them through the log's file lock made the
    /// tray icon feel debounced by ~2 seconds per click.
    /// </summary>
    public static void InstallFirstChanceLogging()
    {
        if (Interlocked.Exchange(ref _processExitInstalled, 1) == 1) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Write("INFO", "Process exiting."); } catch { }
        };
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);

                if (File.Exists(LogPath))
                {
                    var len = new FileInfo(LogPath).Length;
                    if (len > MaxBytes)
                    {
                        // Cheap rotation: drop the file and start over. Anything
                        // worth keeping has already been picked up by the user.
                        File.Delete(LogPath);
                    }
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] T{Thread.CurrentThread.ManagedThreadId,-3} {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging must never throw — last resort is the debugger.
        }
        Debug.WriteLine($"[{level}] {message}");
    }
}
