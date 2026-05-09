using System;
using System.Threading;

namespace Twenti.Services;

/// <summary>
/// Single-instance gate plus a cross-process "wake the running instance"
/// channel built on a named EventWaitHandle.
///
/// Layout:
///   - <see cref="Acquire"/> takes the mutex; on contention it signals the
///     existing instance via <see cref="EventName"/> so the running app can
///     surface a "Twenti is already running" toast, then returns false.
///   - The owning instance calls <see cref="StartListener"/> once and is
///     fired via <paramref name="onSignaled"/> whenever a duplicate launch
///     attempt happens.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Twenti.SingleInstance.{6c3b2a91-4fa2-4e7b-9d8c-twentiapp}";
    private const string EventName = "Twenti.SecondInstanceSignal.{6c3b2a91-4fa2-4e7b-9d8c-twentiapp}";

    private static Mutex? _mutex;
    private static EventWaitHandle? _signal;
    private static Thread? _listener;
    private static volatile bool _stopListener;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool owned);
        if (owned)
        {
            return true;
        }

        // Someone else holds it. Open the signal handle and pulse it so the
        // running instance can show its "already running" toast.
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }
        catch
        {
            // If we can't signal, we still exit cleanly — the user just
            // doesn't see the prompt.
        }
        return false;
    }

    public static void StartListener(Action onSignaled)
    {
        try
        {
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            Logger.Warn($"SingleInstance signal create failed: {ex.Message}");
            return;
        }

        _listener = new Thread(() =>
        {
            while (!_stopListener)
            {
                try
                {
                    if (_signal.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        if (_stopListener) return;
                        try { onSignaled(); }
                        catch (Exception ex) { Logger.Error("SingleInstance.onSignaled threw", ex); }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("SingleInstance listener loop", ex);
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Twenti.SingleInstance.Listener",
        };
        _listener.Start();
    }

    public static void Shutdown()
    {
        _stopListener = true;
        try { _signal?.Set(); } catch { /* swallow */ }
        try { _signal?.Dispose(); } catch { /* swallow */ }
        try { _mutex?.ReleaseMutex(); } catch { /* swallow — may already be released on exit */ }
        try { _mutex?.Dispose(); } catch { /* swallow */ }
    }
}
