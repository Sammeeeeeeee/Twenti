using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Twenti.Services;
using WinRT;

namespace Twenti;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Logger.Info($"Twenti starting (v{UpdateChecker.CurrentVersion})");

        if (!SingleInstance.TryAcquire())
        {
            Logger.Info("Another Twenti instance is running — signalled it and exiting.");
            return 0;
        }

        try
        {
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                // Inner try wraps the App ctor specifically. Application.Start
                // takes the callback on its own message-loop thread, and an
                // exception here would otherwise be swallowed by the loop
                // and leave the user with the silent "thinks then closes"
                // behaviour. Reporting + Environment.Exit gets us a visible
                // failure mode.
                try
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                }
                catch (Exception ex)
                {
                    Logger.Error("Twenti failed to construct the app", ex);
                    ShowFatalError("Twenti failed to construct the app", ex);
                    Environment.Exit(1);
                }
            });
        }
        catch (Exception ex)
        {
            // Outer try covers ComWrappers init / Application.Start itself —
            // these run on the Main thread before the WinAppSDK message loop
            // is up, so a normal exception here also vanishes silently.
            Logger.Error("Twenti failed to start", ex);
            ShowFatalError("Twenti failed to start", ex);
            return 1;
        }

        SingleInstance.Shutdown();
        return 0;
    }

    private static void ShowFatalError(string title, Exception ex)
    {
        try
        {
            const uint MB_ICONERROR = 0x00000010;
            string body = $"{title}\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}\n\nLog file:\n{Logger.LogPath}";
            MessageBoxW(IntPtr.Zero, body, "Twenti", MB_ICONERROR);
        }
        catch
        {
            // Last-ditch — if even the message box fails there's nothing
            // useful we can do.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
