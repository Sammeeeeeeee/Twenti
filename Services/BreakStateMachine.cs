using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace Twenti.Services;

public enum Phase
{
    Working,
    PrePing,
    Alert,
    Break,
    Snoozed,
}

public sealed class TimingProfile
{
    public int WorkSec { get; init; }
    public int ShortBreakSec { get; init; }
    public int LongBreakSec { get; init; }
    public int PrePingLeadSec { get; init; }

    public static TimingProfile Standard(int workMinutes) => new()
    {
        WorkSec = workMinutes * 60,
        ShortBreakSec = 20,
        LongBreakSec = 120,
        PrePingLeadSec = 5,
    };
}

public sealed class BreakStateMachine : INotifyPropertyChanged
{
    private const int AutoSnoozeSec = 12;

    private readonly DispatcherQueue _ui;
    private DispatcherQueueTimer? _timer;

    private Phase _phase = Phase.Working;
    private int _workLeft;
    private int _breakLeft;
    private int _snoozeLeft;
    private int _autoSnoozeLeft;
    private int _cycle = 1;
    private bool _prePingFired;
    private bool _longBreakSkipped;
    private TimingProfile _timing;
    private int _workMinutes = 20;

    public BreakStateMachine(DispatcherQueue ui)
    {
        _ui = ui;
        _timing = TimingProfile.Standard(_workMinutes);
        _workLeft = _timing.WorkSec;
        _breakLeft = _timing.ShortBreakSec;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<Phase>? PhaseChanged;
    public event EventHandler? BreakCompleted;

    public Phase Phase
    {
        get => _phase;
        private set
        {
            if (_phase == value) return;
            _phase = value;
            OnChanged();
            PhaseChanged?.Invoke(this, value);
        }
    }

    public int WorkLeftSec   { get => _workLeft;       private set { if (_workLeft != value)    { _workLeft = value;    OnChanged(); } } }
    public int BreakLeftSec  { get => _breakLeft;      private set { if (_breakLeft != value)   { _breakLeft = value;   OnChanged(); } } }
    public int SnoozeLeftSec { get => _snoozeLeft;     private set { if (_snoozeLeft != value)  { _snoozeLeft = value;  OnChanged(); } } }
    public int AutoSnoozeLeftSec { get => _autoSnoozeLeft; private set { if (_autoSnoozeLeft != value) { _autoSnoozeLeft = value; OnChanged(); } } }
    public int Cycle         { get => _cycle;          private set { if (_cycle != value)      { _cycle = value;       OnChanged(); } } }

    public bool IsLongBreak => Cycle == 3 && !_longBreakSkipped;
    public int CurrentBreakTotalSec => IsLongBreak ? _timing.LongBreakSec : _timing.ShortBreakSec;
    public int WorkTotalSec => _timing.WorkSec;

    public int WorkMinutes
    {
        get => _workMinutes;
        set
        {
            value = Math.Clamp(value, 1, 60);
            if (_workMinutes == value) return;
            _workMinutes = value;
            _timing = TimingProfile.Standard(value);
            ResetToWorking();
            OnChanged();
        }
    }

    public string TooltipText => Phase switch
    {
        Phase.Working when WorkLeftSec >= 60 => $"Next break in {WorkLeftSec / 60} min",
        Phase.Working                        => $"Next break in {WorkLeftSec}s",
        Phase.PrePing                        => $"Break in {WorkLeftSec}s",
        Phase.Alert                          => IsLongBreak ? "Long break" : "Eye break",
        Phase.Break                          => $"{(IsLongBreak ? "Long break" : "Eye break")} · {BreakLeftSec}s left",
        Phase.Snoozed                        => $"Snoozed for: {SnoozeLeftSec / 60}m {SnoozeLeftSec % 60}s",
        _ => "",
    };

    public void Start()
    {
        if (_timer != null) return;
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>
    /// Freezes the countdown — used when the session locks or the system
    /// suspends so the user doesn't lose their place / wake to a stale phase.
    /// </summary>
    public void Pause() => _timer?.Stop();

    public void Resume() => _timer?.Start();

    private void ResetToWorking()
    {
        Phase = Phase.Working;
        WorkLeftSec = _timing.WorkSec;
        BreakLeftSec = _timing.ShortBreakSec;
        SnoozeLeftSec = 0;
        _prePingFired = false;
        _longBreakSkipped = false;
    }

    private void Tick()
    {
        switch (Phase)
        {
            case Phase.Working:
            case Phase.PrePing:
                TickWorking();
                break;
            case Phase.Alert:
                TickAlert();
                break;
            case Phase.Break:
                TickBreak();
                break;
            case Phase.Snoozed:
                TickSnoozed();
                break;
        }
    }

    private void TickWorking()
    {
        WorkLeftSec--;
        if (!_prePingFired && WorkLeftSec == _timing.PrePingLeadSec)
        {
            _prePingFired = true;
            Phase = Phase.PrePing;
        }
        if (WorkLeftSec <= 0)
        {
            BreakLeftSec = CurrentBreakTotalSec;
            AutoSnoozeLeftSec = AutoSnoozeSec;
            Phase = Phase.Alert;
        }
    }

    private void TickAlert()
    {
        AutoSnoozeLeftSec--;
        if (AutoSnoozeLeftSec <= 0)
        {
            Snooze(5);
        }
    }

    private void TickBreak()
    {
        BreakLeftSec--;
        if (BreakLeftSec <= 0)
        {
            BreakCompleted?.Invoke(this, EventArgs.Empty);
            AdvanceCycle();
            ResetToWorking();
        }
    }

    private void TickSnoozed()
    {
        SnoozeLeftSec--;
        if (SnoozeLeftSec <= 0)
        {
            BreakLeftSec = CurrentBreakTotalSec;
            AutoSnoozeLeftSec = AutoSnoozeSec;
            Phase = Phase.Alert;
        }
    }

    private void AdvanceCycle()
    {
        Cycle = Cycle >= 3 ? 1 : Cycle + 1;
    }

    public void StartBreak()
    {
        if (Phase is not (Phase.Alert or Phase.Snoozed)) return;
        BreakLeftSec = CurrentBreakTotalSec;
        Phase = Phase.Break;
    }

    /// <summary>
    /// Demote this cycle's long break to a 20-second short break. Behaviour
    /// depends on the current phase:
    ///   • Alert/Snoozed — the pending long break converts to a fresh 20s
    ///     break and starts immediately.
    ///   • Break — the user is already partway through the long break;
    ///     credit the time already served against the 20s short, so e.g.
    ///     5s done → 15s remaining. If they've already done ≥20s, the
    ///     break ends immediately.
    /// Cycle count is preserved either way so the next long break still
    /// lands on the regular cadence; the skip flag clears once the break
    /// finishes.
    /// </summary>
    public void SkipLongBreak()
    {
        if (Cycle != 3 || _longBreakSkipped) return;

        if (Phase is Phase.Alert or Phase.Snoozed)
        {
            _longBreakSkipped = true;
            BreakLeftSec = _timing.ShortBreakSec;
            OnChanged(nameof(IsLongBreak));
            OnChanged(nameof(CurrentBreakTotalSec));
            // Jump straight into the break — gives the user an unambiguous
            // visual ack that Del took effect (countdown screen appears).
            Phase = Phase.Break;
            return;
        }

        if (Phase == Phase.Break)
        {
            int elapsed = _timing.LongBreakSec - BreakLeftSec;
            int remaining = Math.Max(0, _timing.ShortBreakSec - elapsed);
            _longBreakSkipped = true;
            BreakLeftSec = remaining;
            OnChanged(nameof(IsLongBreak));
            OnChanged(nameof(CurrentBreakTotalSec));
            if (remaining <= 0)
            {
                BreakCompleted?.Invoke(this, EventArgs.Empty);
                AdvanceCycle();
                ResetToWorking();
            }
        }
    }

    public void TriggerBreakNow()
    {
        BreakLeftSec = CurrentBreakTotalSec;
        AutoSnoozeLeftSec = AutoSnoozeSec;
        Phase = Phase.Alert;
    }

    public void Snooze(int minutes)
    {
        SnoozeLeftSec = minutes * 60;
        Phase = Phase.Snoozed;
    }

    private void OnChanged([CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        if (property is nameof(Phase) or nameof(WorkLeftSec) or nameof(BreakLeftSec) or nameof(SnoozeLeftSec))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TooltipText)));
        }
    }
}
