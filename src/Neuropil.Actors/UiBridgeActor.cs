using System.Diagnostics;
using System.Threading.Channels;
using Akka.Actor;
using Neuropil.Core;

namespace Neuropil.Actors;

/// <summary>What the UI sees: everything since the last frame, plus firing rates over the last 100 ms of simulated time.</summary>
public sealed record Frame(
    long Step,
    double TimeMs,
    bool Running,
    double? Speed,
    IReadOnlyList<(long Step, int Neuron)> Spikes,
    IReadOnlyList<(int Neuron, double Hz)> Rates,
    bool Truncated);

/// <summary>
/// Records spikes from the clock and forwards them to the UI at a fixed frame rate, however
/// fast the simulation runs.
/// </summary>
public sealed class UiBridgeActor : ReceiveActor, IWithTimers
{
    sealed record Flush;

    public const int FramesPerSecond = 30;
    public const int MaxSpikesPerFrame = 5000;
    const double RateWindowMs = 100;
    const double FrameMs = 1000.0 / FramesPerSecond;
    const double TimerMs = 10;

    readonly LifParams _p;
    readonly ChannelWriter<Frame> _frames;
    readonly long _windowSteps;
    readonly int[] _windowCounts;
    readonly Queue<(long Step, int Neuron)> _window = new();
    readonly List<(long Step, int Neuron)> _unsent = [];

    readonly Stopwatch _wall = Stopwatch.StartNew();
    double _nextFrameMs;

    long _step;
    bool _running, _truncated, _dirty = true;
    double? _speed;

    public ITimerScheduler Timers { get; set; } = null!;

    public UiBridgeActor(int neurons, LifParams p, ChannelWriter<Frame> frames)
    {
        _p = p;
        _frames = frames;
        _windowSteps = (long)Math.Round(RateWindowMs / p.Dt);
        _windowCounts = new int[neurons];

        Receive<EpochSpikes>(OnEpoch);
        Receive<PlaybackChanged>(m => { (_running, _speed) = (m.Running, m.Speed); _dirty = true; });
        Receive<Reset>(_ =>
        {
            _step = 0;
            _unsent.Clear();
            _window.Clear();
            Array.Clear(_windowCounts);
            _truncated = false;
            _dirty = true;
        });
        Receive<Flush>(_ => OnFlush());
    }

    protected override void PreStart() =>
        Timers.StartPeriodicTimer("flush", new Flush(), TimeSpan.FromMilliseconds(TimerMs));

    void OnEpoch(EpochSpikes e)
    {
        _step = e.ToStep;
        _dirty = true;
        foreach (var spike in e.Spikes)
        {
            _window.Enqueue(spike);
            _windowCounts[spike.Neuron]++;
            if (_unsent.Count < MaxSpikesPerFrame) _unsent.Add(spike); else _truncated = true;
        }
        while (_window.TryPeek(out var old) && old.Step < _step - _windowSteps)
        {
            _window.Dequeue();
            _windowCounts[old.Neuron]--;
        }
    }

    void OnFlush()
    {
        // The scheduler's ticks are coarse, so the frame rate is held by the wall clock, not the timer.
        double now = _wall.Elapsed.TotalMilliseconds;
        if (!_dirty || now < _nextFrameMs) return;
        _nextFrameMs = Math.Max(_nextFrameMs, now - TimerMs) + FrameMs;

        var rates = new List<(int, double)>();
        for (int i = 0; i < _windowCounts.Length; i++)
            if (_windowCounts[i] > 0) rates.Add((i, _windowCounts[i] * 1000 / RateWindowMs));

        _frames.TryWrite(new Frame(_step, _step * _p.Dt, _running, _speed, [.. _unsent], rates, _truncated));
        _unsent.Clear();
        _truncated = false;
        _dirty = false;
    }
}
