using Akka.Actor;
using Neuropil.Core;

namespace Neuropil.Actors;

/// <summary>Which neurons each body sensor drives, and which neurons each body action reads.</summary>
public sealed record BodyPlan(
    IReadOnlyDictionary<string, (IReadOnlyList<int> Neurons, double MaxRateHz)> Sensors,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Motors);

// What the body feels right now: a level from 0 to 1 per sensor.
public sealed record Sense(IReadOnlyDictionary<string, double> Levels);

public sealed record MotorRates(long Step, IReadOnlyList<(string Motor, double Hz)> Rates);

public sealed record AttachClock(IActorRef Clock);

/// <summary>
/// The loop between brain and body. Every 10 ms of simulated time it turns the latest sensor
/// levels into stimulation of the matching sensory neurons, and reports how fast each group of
/// motor neurons has been firing.
/// </summary>
public sealed class BodyBridgeActor : ReceiveActor
{
    public const double PeriodMs = 10;
    const double WindowMs = 50;

    readonly BodyPlan _plan;
    readonly IActorRef _output;
    readonly long _periodSteps, _windowSteps;
    readonly Dictionary<int, string> _motorOf = [];
    readonly Dictionary<string, Queue<long>> _recent = [];
    readonly Dictionary<string, double> _levels = [], _applied = [];
    IActorRef _clock = ActorRefs.Nobody;

    public BodyBridgeActor(BodyPlan plan, LifParams p, IActorRef output)
    {
        _plan = plan;
        _output = output;
        _periodSteps = (long)Math.Round(PeriodMs / p.Dt);
        _windowSteps = (long)Math.Round(WindowMs / p.Dt);
        foreach (var (motor, neurons) in plan.Motors)
        {
            _recent[motor] = new Queue<long>();
            foreach (int neuron in neurons) _motorOf[neuron] = motor;
        }

        Receive<AttachClock>(m =>
        {
            _clock = m.Clock;
            _clock.Tell(new Subscribe(Self));
            _applied.Clear();
            foreach (var spikes in _recent.Values) spikes.Clear();
        });
        Receive<Sense>(m =>
        {
            foreach (var (sensor, level) in m.Levels)
                if (_plan.Sensors.ContainsKey(sensor)) _levels[sensor] = Math.Clamp(level, 0, 1);
        });
        Receive<EpochSpikes>(OnEpoch);
    }

    void OnEpoch(EpochSpikes e)
    {
        foreach (var (step, neuron) in e.Spikes)
            if (_motorOf.TryGetValue(neuron, out var motor)) _recent[motor].Enqueue(step);

        if (e.ToStep / _periodSteps == e.FromStep / _periodSteps) return;

        var rates = new List<(string, double)>();
        foreach (var (motor, spikes) in _recent)
        {
            while (spikes.TryPeek(out long step) && step < e.ToStep - _windowSteps) spikes.Dequeue();
            rates.Add((motor, spikes.Count / (_plan.Motors[motor].Count * WindowMs / 1000)));
        }
        _output.Tell(new MotorRates(e.ToStep, rates));

        foreach (var (sensor, level) in _levels)
        {
            if (_applied.GetValueOrDefault(sensor) == level) continue;
            _applied[sensor] = level;
            var (neurons, maxRate) = _plan.Sensors[sensor];
            _clock.Tell(new Stimulate(neurons, level * maxRate));
        }
    }
}
