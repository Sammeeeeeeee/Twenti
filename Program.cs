using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Twenti;

public static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        // Mutex is keyed by a fixed GUID, NOT the EXE filename, so the
        // single-instance check works whether the EXE is "Twenti.exe", a
        // renamed copy, or run from a different folder.
        _singleInstanceMutex = new Mutex(true, "Twenti.SingleInstance.{6c3b2a91-4fa2-4e7b-9d8c-twentiapp}", out var owned);
        if (!owned)
        {
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
            ShowFatalError("Twenti failed to start", ex);
            return 1;
        }

        GC.KeepAlive(_singleInstanceMutex);
        return 0;
    }

    private static void ShowFatalError(string title, Exception ex)
    {
        try
        {
            const uint MB_ICONERROR = 0x00000010;
            string body = $"{title}\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
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
