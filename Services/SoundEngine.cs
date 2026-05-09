using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Twenti.Services;

public readonly record struct AudioDevice(int DeviceNumber, string Name)
{
    public bool IsDefault => DeviceNumber < 0;
}

public sealed class SoundEngine : IDisposable
{
    private const int SampleRate = 44100;
    private const int Channels = 2;

    private WaveOutEvent _cueOut;
    private readonly MixingSampleProvider _cueMixer;
    private WaveOutEvent? _ambientOut;
    private bool _disposed;
    private bool _cueStarted;
    private int _deviceNumber = -1; // -1 = system default

    public bool Muted { get; set; }

    public SoundEngine()
    {
        // Mixer is cheap; the WaveOut device init is the slow bit and gets
        // deferred to the first time we actually need to play a sound. Keeps
        // app startup snappy.
        _cueMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true,
        };
        _cueOut = new WaveOutEvent { DesiredLatency = 80, DeviceNumber = _deviceNumber };
    }

    /// <summary>
    /// Enumerate available WaveOut devices. The first entry is always the
    /// "system default" sentinel with DeviceNumber = -1.
    /// </summary>
    public static IReadOnlyList<AudioDevice> EnumerateDevices()
    {
        var list = new List<AudioDevice> { new(-1, "System default") };
        try
        {
            int n = WaveOut.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var caps = WaveOut.GetCapabilities(i);
                    list.Add(new AudioDevice(i, caps.ProductName));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"WaveOut.GetCapabilities({i}) failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"WaveOut device enumeration failed: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Switch the output device. Existing playback is torn down and the
    /// next cue/ambient will route to the new device.
    /// </summary>
    public void SetOutputDeviceByName(string? deviceName)
    {
        int target = -1;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            foreach (var d in EnumerateDevices())
            {
                if (!d.IsDefault && string.Equals(d.Name, deviceName, StringComparison.Ordinal))
                {
                    target = d.DeviceNumber;
                    break;
                }
            }
            // Fall through to default if name not found (device unplugged etc.).
        }
        if (target == _deviceNumber) return;

        _deviceNumber = target;
        try
        {
            try { _cueOut.Stop(); _cueOut.Dispose(); } catch { /* swallow */ }
            _cueOut = new WaveOutEvent { DesiredLatency = 80, DeviceNumber = _deviceNumber };
            _cueStarted = false;
            // Ambient is restarted lazily; if it was running, drop it — the
            // next break will spin up a fresh provider on the new device.
            StopAmbient();
        }
        catch (Exception ex)
        {
            Logger.Error("SoundEngine.SetOutputDeviceByName failed", ex);
        }
    }

    public string? CurrentDeviceName
    {
        get
        {
            if (_deviceNumber < 0) return null;
            try { return WaveOut.GetCapabilities(_deviceNumber).ProductName; }
            catch { return null; }
        }
    }

    private void EnsureCueStarted()
    {
        if (_cueStarted || _disposed) return;
        _cueStarted = true;
        try
        {
            _cueOut.Init(_cueMixer);
            _cueOut.Play();
        }
        catch (Exception ex)
        {
            Logger.Warn($"SoundEngine cue init failed: {ex.Message}");
            // Leave _cueStarted true so we don't keep retrying.
        }
    }

    public void PlayPrePing()
    {
        if (Muted) return;
        AddTone(1318.5, volume: 0.12, duration: 0.35, delay: 0);
        AddTone(1318.5, volume: 0.05, duration: 0.6,  delay: 0.05);
    }

    public void PlayPopupChime()
    {
        if (Muted) return;
        AddTone(659.25, volume: 0.18, duration: 0.55, delay: 0);
        AddTone(880.00, volume: 0.13, duration: 0.50, delay: 0.14);
        AddTone(1046.50, volume: 0.08, duration: 0.40, delay: 0.28);
    }

    public void PlaySnooze()
    {
        if (Muted) return;
        AddTone(523.25, volume: 0.12, duration: 0.30, delay: 0);
        AddTone(415.30, volume: 0.09, duration: 0.30, delay: 0.18);
    }

    public void PlayBreakComplete()
    {
        if (Muted) return;
        AddTone(523.25, volume: 0.14, duration: 0.35, delay: 0);
        AddTone(659.25, volume: 0.12, duration: 0.35, delay: 0.12);
        AddTone(783.99, volume: 0.16, duration: 0.50, delay: 0.24, vibratoHz: 5, vibratoCents: 12);
    }

    public void StartAmbient()
    {
        if (Muted) return;
        StopAmbient();
        var src = new BrownWaterSampleProvider(SampleRate, Channels)
        {
            TargetVolume = 0.22f,
            FadeInSeconds = 2.0f,
        };
        try
        {
            _ambientOut = new WaveOutEvent { DesiredLatency = 200, DeviceNumber = _deviceNumber };
            _ambientOut.Init(src);
            _ambientOut.Play();
        }
        catch (Exception ex)
        {
            Logger.Warn($"SoundEngine ambient start failed: {ex.Message}");
            _ambientOut = null;
        }
    }

    public void StopAmbient()
    {
        if (_ambientOut is null) return;
        try { _ambientOut.Stop(); _ambientOut.Dispose(); } catch { /* swallow */ }
        _ambientOut = null;
    }

    private void AddTone(double freq, double volume, double duration, double delay, double vibratoHz = 0, double vibratoCents = 0)
    {
        EnsureCueStarted();
        var tone = new EnvelopedToneProvider(SampleRate, Channels)
        {
            Frequency = freq,
            Volume = (float)volume,
            DurationSeconds = (float)duration,
            DelaySeconds = (float)delay,
            VibratoHz = (float)vibratoHz,
            VibratoCents = (float)vibratoCents,
        };
        _cueMixer.AddMixerInput(tone);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAmbient();
        try { _cueOut.Stop(); _cueOut.Dispose(); } catch { /* swallow */ }
    }
}

internal sealed class EnvelopedToneProvider : ISampleProvider
{
    private readonly Random _rng = new();
    private double _phase;
    private int _samplesElapsed;

    public WaveFormat WaveFormat { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public double Frequency { get; set; }
    public float Volume { get; set; } = 0.1f;
    public float DurationSeconds { get; set; }
    public float DelaySeconds { get; set; }
    public float VibratoHz { get; set; }
    public float VibratoCents { get; set; }

    private const float AttackSec = 0.018f;

    public EnvelopedToneProvider(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / Channels;
        int delaySamples = (int)(DelaySeconds * SampleRate);
        int totalSamples = (int)((DelaySeconds + DurationSeconds + 0.05f) * SampleRate);
        int written = 0;

        for (int f = 0; f < frames; f++)
        {
            float sample = 0f;

            if (_samplesElapsed >= delaySamples)
            {
                int activeSample = _samplesElapsed - delaySamples;
                float t = activeSample / (float)SampleRate;

                if (t <= DurationSeconds)
                {
                    float env;
                    if (t < AttackSec)
                    {
                        env = t / AttackSec;
                    }
                    else
                    {
                        float k = MathF.Log(0.0001f) / DurationSeconds;
                        env = MathF.Exp(k * t);
                    }

                    double freq = Frequency;
                    if (VibratoHz > 0 && VibratoCents > 0)
                    {
                        double mod = Math.Sin(2 * Math.PI * VibratoHz * t);
                        double semitones = (VibratoCents * mod) / 100.0;
                        freq = Frequency * Math.Pow(2.0, semitones / 12.0);
                    }

                    _phase += 2 * Math.PI * freq / SampleRate;
                    if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;

                    sample = (float)(Math.Sin(_phase) * Volume * env);
                }
            }

            for (int c = 0; c < Channels; c++)
            {
                buffer[offset + f * Channels + c] = sample;
            }
            written += Channels;
            _samplesElapsed++;

            if (_samplesElapsed >= totalSamples)
            {
                for (int rest = (f + 1) * Channels; rest < count; rest++)
                {
                    buffer[offset + rest] = 0f;
                }
                return count;
            }
        }
        return written;
    }
}

internal sealed class BrownWaterSampleProvider : ISampleProvider
{
    private readonly Random _rng = new();
    private float _last;
    private readonly BiQuadFilter _lp1Left, _lp1Right;
    private readonly BiQuadFilter _lp2Left, _lp2Right;
    private readonly BiQuadFilter _bpLeft, _bpRight;
    private int _samplesElapsed;

    public WaveFormat WaveFormat { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public float TargetVolume { get; set; } = 0.22f;
    public float FadeInSeconds { get; set; } = 2.0f;

    public BrownWaterSampleProvider(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        _lp1Left  = BiQuadFilter.LowPassFilter(sampleRate, 600f, 0.6f);
        _lp1Right = BiQuadFilter.LowPassFilter(sampleRate, 600f, 0.6f);
        _lp2Left  = BiQuadFilter.LowPassFilter(sampleRate, 300f, 0.4f);
        _lp2Right = BiQuadFilter.LowPassFilter(sampleRate, 300f, 0.4f);
        _bpLeft   = BiQuadFilter.BandPassFilterConstantSkirtGain(sampleRate, 200f, 1.2f);
        _bpRight  = BiQuadFilter.BandPassFilterConstantSkirtGain(sampleRate, 200f, 1.2f);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / Channels;
        for (int f = 0; f < frames; f++)
        {
            float white = (float)(_rng.NextDouble() * 2 - 1);
            _last = (_last + 0.018f * white) / 1.018f;
            float brown = _last * 4f;

            float left  = _bpLeft.Transform(_lp2Left.Transform(_lp1Left.Transform(brown)));
            float right = _bpRight.Transform(_lp2Right.Transform(_lp1Right.Transform(brown)));

            float t = _samplesElapsed / (float)SampleRate;
            float fade = FadeInSeconds <= 0 ? 1f : Math.Min(1f, t / FadeInSeconds);
            float vol = TargetVolume * fade;

            buffer[offset + f * Channels + 0] = left * vol;
            if (Channels > 1)
                buffer[offset + f * Channels + 1] = right * vol;

            _samplesElapsed++;
        }
        return count;
    }
}
