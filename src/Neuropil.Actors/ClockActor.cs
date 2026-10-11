using Akka.Actor;
using Neuropil.Core;

namespace Neuropil.Actors;

public sealed class ClockActor : ReceiveActor
{
    /// <param name="RegionOf">Region index per neuron for region mode; null runs one actor per neuron.</param>
    public sealed record Setup(
        Brain Brain,
        LifParams P,
        ulong Seed,
        int Trial,
        IReadOnlyList<int> Stimulated,
        double? RateHz,
        IReadOnlyList<int> Silenced,
        int[]? RegionOf = null);

    readonly IActorRef[] _units;
    readonly int[] _unitOf;
    readonly int _epochSteps;
    readonly bool[] _active, _nextActive;

    long _step, _endStep;
    int _outstanding;
    IActorRef _caller = ActorRefs.Nobody;
    IActorRef? _subscriber;
    int? _watched;
    double[]? _voltages;
    readonly LifParams _p;
    readonly List<(long Step, int Neuron)> _spikes = [];
    readonly List<(long Step, int Neuron)> _epochSpikes = [];

    public ClockActor(Setup s)
    {
        int n = s.Brain.NeuronCount;
        _p = s.P;
        _epochSteps = s.P.DelaySteps;
        ulong threshold = s.P.PoissonThreshold(s.RateHz);
        var stimulated = s.Stimulated.ToHashSet();
        var silenced = s.Silenced.ToHashSet();

        if (s.RegionOf is { } regionOf)
        {
            var members = Enumerable.Range(0, n).GroupBy(i => regionOf[i]).OrderBy(g => g.Key).Select(g => g.ToArray()).ToArray();
            _unitOf = new int[n];
            var localIndex = new int[n];
            for (int r = 0; r < members.Length; r++)
                for (int k = 0; k < members[r].Length; k++)
                    (_unitOf[members[r][k]], localIndex[members[r][k]]) = (r, k);

            _units = new IActorRef[members.Length];
            for (int r = 0; r < members.Length; r++)
            {
                var setup = new RegionActor.Setup(s.Brain, r, members[r], _unitOf, localIndex, _units, s.P, s.Seed, s.Trial,
                    stimulated, threshold, silenced);
                _units[r] = Context.ActorOf(Props.Create(() => new RegionActor(setup)), $"r{r}");
            }
        }
        else
        {
            _unitOf = [.. Enumerable.Range(0, n)];
            _units = new IActorRef[n];
            for (int i = 0; i < n; i++)
            {
                var setup = new NeuronActor.Setup(s.Brain, i, _units, s.P, s.Seed, s.Trial,
                    stimulated.Contains(i) ? threshold : 0, silenced.Contains(i));
                _units[i] = Context.ActorOf(Props.Create(() => new NeuronActor(setup)), $"n{i}");
            }
        }

        _active = new bool[_units.Length];
        _nextActive = new bool[_units.Length];
        Array.Fill(_active, true);

        Receive<Run>(r =>
        {
            _caller = Sender;
            _endStep = _step + r.Steps;
            _spikes.Clear();
            StartEpoch();
        });
        Receive<EpochDone>(OnDone);
        Receive<Subscribe>(m => _subscriber = m.Subscriber);
        Receive<Stimulate>(OnStimulate);
        Receive<Watch>(OnWatch);
    }

    void Wake(int neuron) => (_outstanding > 0 ? _nextActive : _active)[_unitOf[neuron]] = true;

    void OnWatch(Watch m)
    {
        if (_watched is { } old) _units[_unitOf[old]].Tell(new SetWatched(old, false));
        _watched = m.Neuron;
        if (m.Neuron is not { } i) return;
        _units[_unitOf[i]].Tell(new SetWatched(i, true));
        Wake(i);
    }

    void OnStimulate(Stimulate m)
    {
        ulong threshold = m.RateHz > 0 ? _p.PoissonThreshold(m.RateHz) : 0;
        foreach (int i in m.Neurons)
        {
            _units[_unitOf[i]].Tell(new SetStimulus(i, threshold));
            Wake(i);
        }
    }

    void StartEpoch()
    {
        while (_step < _endStep)
        {
            var epoch = new RunEpoch(_step, Math.Min(_step + _epochSteps, _endStep));
            _outstanding = 0;
            for (int u = 0; u < _units.Length; u++)
            {
                if (!_active[u]) continue;
                _units[u].Tell(epoch);
                _outstanding++;
            }
            if (_outstanding > 0) return;
            _step = epoch.ToStep;
            _subscriber?.Tell(new EpochSpikes(epoch.FromStep, epoch.ToStep, []));
        }

        _caller.Tell(new RunResult([.. _spikes]));
    }

    void OnDone(EpochDone d)
    {
        _epochSpikes.AddRange(d.Spikes);
        if (d.Voltages is not null) _voltages = d.Voltages;
        if (d.Active) _nextActive[d.Unit] = true;
        foreach (int w in d.Woken) _nextActive[w] = true;
        if (--_outstanding > 0) return;

        long from = _step;
        _step = Math.Min(_step + _epochSteps, _endStep);
        _epochSpikes.Sort();
        _spikes.AddRange(_epochSpikes);
        _subscriber?.Tell(new EpochSpikes(from, _step, [.. _epochSpikes], _voltages is null ? null : _watched, _voltages));
        _voltages = null;
        _epochSpikes.Clear();
        Array.Copy(_nextActive, _active, _active.Length);
        Array.Clear(_nextActive);
        StartEpoch();
    }
}
