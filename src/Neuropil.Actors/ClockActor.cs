using Akka.Actor;
using Neuropil.Core;

namespace Neuropil.Actors;

public sealed class ClockActor : ReceiveActor
{
    public sealed record Setup(
        Brain Brain,
        LifParams P,
        ulong Seed,
        int Trial,
        IReadOnlyList<int> Stimulated,
        double? RateHz,
        IReadOnlyList<int> Silenced);

    readonly IActorRef[] _neurons;
    readonly int _epochSteps;
    readonly bool[] _active, _nextActive;

    long _step, _endStep;
    int _outstanding;
    IActorRef _caller = ActorRefs.Nobody;
    readonly List<(long Step, int Neuron)> _spikes = [];

    public ClockActor(Setup s)
    {
        int n = s.Brain.NeuronCount;
        _epochSteps = s.P.DelaySteps;
        _neurons = new IActorRef[n];
        _active = new bool[n];
        _nextActive = new bool[n];
        Array.Fill(_active, true);

        var stimulated = s.Stimulated.ToHashSet();
        var silenced = s.Silenced.ToHashSet();
        ulong threshold = s.P.PoissonThreshold(s.RateHz);
        for (int i = 0; i < n; i++)
        {
            var setup = new NeuronActor.Setup(s.Brain, i, _neurons, s.P, s.Seed, s.Trial,
                stimulated.Contains(i) ? threshold : 0, silenced.Contains(i));
            _neurons[i] = Context.ActorOf(Props.Create(() => new NeuronActor(setup)), $"n{i}");
        }

        Receive<Run>(r =>
        {
            _caller = Sender;
            _endStep = _step + r.Steps;
            _spikes.Clear();
            StartEpoch();
        });
        Receive<EpochDone>(OnDone);
    }

    void StartEpoch()
    {
        while (_step < _endStep)
        {
            var epoch = new RunEpoch(_step, Math.Min(_step + _epochSteps, _endStep));
            _outstanding = 0;
            for (int i = 0; i < _neurons.Length; i++)
            {
                if (!_active[i]) continue;
                _neurons[i].Tell(epoch);
                _outstanding++;
            }
            if (_outstanding > 0) return;
            _step = epoch.ToStep;
        }

        _spikes.Sort();
        _caller.Tell(new RunResult([.. _spikes]));
    }

    void OnDone(EpochDone d)
    {
        foreach (long t in d.Spikes) _spikes.Add((t, d.Neuron));
        if (d.Active) _nextActive[d.Neuron] = true;
        foreach (int w in d.Woken) _nextActive[w] = true;
        if (--_outstanding > 0) return;

        _step = Math.Min(_step + _epochSteps, _endStep);
        Array.Copy(_nextActive, _active, _active.Length);
        Array.Clear(_nextActive);
        StartEpoch();
    }
}
