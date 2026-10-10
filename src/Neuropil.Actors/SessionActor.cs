using System.Diagnostics;
using System.Threading.Channels;
using Akka.Actor;
using Neuropil.Core;

namespace Neuropil.Actors;

public sealed record Play;
public sealed record Pause;
public sealed record StepOnce(long Steps);
public sealed record Reset;

// Simulated time per wall-clock time: 1 is real time, 0.01 is a hundred times slower, null is flat out.
public sealed record SetSpeed(double? Ratio);

public sealed record PlaybackChanged(bool Running, double? Speed);

/// <summary>
/// One experiment a viewer controls: owns the clock, paces it against the wall clock, and
/// rebuilds it on reset. Spikes go to the UI through a UiBridgeActor.
/// </summary>
public sealed class SessionActor : ReceiveActor, IWithTimers
{
    public sealed record Setup(Brain Brain, LifParams P, ulong Seed, ChannelWriter<Frame> Frames);

    sealed record Continue;

    const double ChunkWallMs = 10;
    const int MaxChunkSteps = 500;
    const double MaxLagMs = 100;

    readonly Setup _s;
    readonly IActorRef _bridge;
    readonly Dictionary<int, double> _stimulus = [];
    readonly Stopwatch _wall = Stopwatch.StartNew();

    IActorRef _clock = ActorRefs.Nobody;
    int _generation;
    long _step;
    bool _running, _busy, _resetPending;
    double? _speed = 0.05;
    long _pendingSteps;

    // Pacing reference: simulated step _anchorStep was due at wall time _anchorMs.
    double _anchorMs;
    long _anchorStep;

    public ITimerScheduler Timers { get; set; } = null!;

    public SessionActor(Setup s)
    {
        _s = s;
        _bridge = Context.ActorOf(Props.Create(() => new UiBridgeActor(s.Brain.NeuronCount, s.P, s.Frames)), "bridge");
        StartClock();
        Announce();

        Receive<Play>(_ => { _running = true; Anchor(); Announce(); Advance(); });
        Receive<Pause>(_ => { _running = false; Announce(); });
        Receive<SetSpeed>(m => { _speed = m.Ratio; Anchor(); Announce(); Advance(); });
        Receive<StepOnce>(m => { if (!_running && !_busy) Run(m.Steps); });
        Receive<Reset>(_ => { if (_busy) _resetPending = true; else DoReset(); });
        Receive<Stimulate>(m =>
        {
            foreach (int i in m.Neurons) _stimulus[i] = m.RateHz;
            _clock.Tell(m);
        });
        Receive<Continue>(_ => Advance());
        Receive<RunResult>(_ =>
        {
            _busy = false;
            _step += _pendingSteps;
            if (_resetPending) DoReset(); else Advance();
        });
    }

    void StartClock()
    {
        var setup = new ClockActor.Setup(_s.Brain, _s.P, _s.Seed, 0, [], null, []);
        _clock = Context.ActorOf(Props.Create(() => new ClockActor(setup)), $"clock-{_generation++}");
        _clock.Tell(new Subscribe(_bridge));
        foreach (var group in _stimulus.GroupBy(kv => kv.Value, kv => kv.Key))
            _clock.Tell(new Stimulate([.. group], group.Key));
    }

    void DoReset()
    {
        _resetPending = false;
        _running = false;
        _step = 0;
        Context.Stop(_clock);
        StartClock();
        _bridge.Tell(new Reset());
        Announce();
    }

    void Anchor()
    {
        _anchorMs = _wall.Elapsed.TotalMilliseconds;
        _anchorStep = _step;
    }

    void Announce() => _bridge.Tell(new PlaybackChanged(_running, _speed));

    void Advance()
    {
        if (!_running || _busy) return;
        if (_speed is not { } speed)
        {
            Run(MaxChunkSteps);
            return;
        }

        double now = _wall.Elapsed.TotalMilliseconds;
        double due = _anchorMs + (_step - _anchorStep) * _s.P.Dt / speed;
        if (now - due > MaxLagMs)
        {
            // The simulation can't keep up at this speed; don't try to make up the lost time.
            Anchor();
            due = now;
        }
        if (due - now > 1)
        {
            Timers.StartSingleTimer("continue", new Continue(), TimeSpan.FromMilliseconds(due - now));
            return;
        }
        Run(Math.Clamp((long)Math.Round(speed * ChunkWallMs / _s.P.Dt), 1, MaxChunkSteps));
    }

    void Run(long steps)
    {
        _busy = true;
        _pendingSteps = steps;
        _clock.Tell(new Run(steps));
    }
}
