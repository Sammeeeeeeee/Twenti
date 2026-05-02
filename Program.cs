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
        _singleInstanceMutex = new Mutex(true, "Twenti.SingleInstance.{6c3b2a91-4fa2-4e7b-9d8c-twentiapp}", out var owned);
        if (!owned)
        {
            return 0;
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        GC.KeepAlive(_singleInstanceMutex);
        return 0;
    }
}
