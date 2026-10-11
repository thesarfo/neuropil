using Akka.Actor;
using Neuropil.Core;

namespace Neuropil.Actors;

/// <summary>
/// Many neurons in one actor. Inside, they are plain arrays stepped like ArraySimulator; spikes
/// between them never become messages. Spikes to other regions leave as one batch per region
/// per epoch.
/// </summary>
public sealed class RegionActor : ReceiveActor
{
    public sealed record Setup(
        Brain Brain,
        int Region,
        int[] Members,
        int[] RegionOf,
        int[] LocalIndex,
        IActorRef[] Regions,
        LifParams P,
        ulong Seed,
        int Trial,
        IReadOnlySet<int> Stimulated,
        ulong PoissonThreshold,
        IReadOnlySet<int> Silenced);

    sealed class Outbox
    {
        public readonly List<int> Targets = [];
        public readonly List<byte> Offsets = [];
        public readonly List<short> Weights = [];
    }

    readonly Setup _s;
    readonly LifParams _p;
    readonly Brain _brain;
    readonly int[] _members;
    readonly int _n, _slots;
    readonly double _decayV, _decayG, _gToV;

    readonly double[] _v, _g;
    readonly long[] _lastSpike;
    readonly bool[] _notRefractory, _silenced;
    readonly int[] _refractorySteps;
    readonly ulong[] _poissonThreshold;
    readonly SortedSet<int> _stimulated = [];

    // Slot (arrival step mod two epochs) * _n + local neuron.
    readonly int[] _pending;

    readonly Outbox?[] _outboxes;
    readonly List<int> _touched = [];
    readonly List<int> _spiked = [];
    readonly List<(long Step, int Neuron)> _spikes = [];
    readonly List<int> _woken = [];

    int _watched = -1;
    double[]? _voltages;
    bool _idle;
    int _awaitingAcks;
    IActorRef _clock = ActorRefs.Nobody;

    public RegionActor(Setup s)
    {
        _s = s;
        _p = s.P;
        _brain = s.Brain;
        _members = s.Members;
        _n = _members.Length;
        _slots = 2 * _p.DelaySteps;
        (_decayV, _decayG, _gToV) = (_p.DecayV, _p.DecayG, _p.GToV);

        _v = new double[_n];
        _g = new double[_n];
        _lastSpike = new long[_n];
        _notRefractory = new bool[_n];
        _silenced = new bool[_n];
        _refractorySteps = new int[_n];
        _poissonThreshold = new ulong[_n];
        _pending = new int[_slots * _n];
        _outboxes = new Outbox?[s.Regions.Length];

        Array.Fill(_v, _p.VRest);
        Array.Fill(_lastSpike, long.MinValue / 2);
        Array.Fill(_notRefractory, true);
        for (int i = 0; i < _n; i++)
        {
            _silenced[i] = s.Silenced.Contains(_members[i]);
            SetStimulus(i, s.Stimulated.Contains(_members[i]) ? s.PoissonThreshold : 0);
        }

        Receive<RunEpoch>(RunEpoch);
        Receive<RegionSpikes>(OnSpikes);
        Receive<SpikeAck>(OnAck);
        Receive<SetStimulus>(m => SetStimulus(s.LocalIndex[m.Neuron], m.PoissonThreshold));
        Receive<SetWatched>(m => _watched = m.Watched ? s.LocalIndex[m.Neuron] : -1);
        Receive<SetSilenced>(m => _silenced[s.LocalIndex[m.Neuron]] = m.Silenced);
    }

    void SetStimulus(int local, ulong threshold)
    {
        _poissonThreshold[local] = threshold;
        _refractorySteps[local] = threshold > 0 ? 0 : _p.RefractorySteps;
        if (threshold > 0) _stimulated.Add(local); else _stimulated.Remove(local);
    }

    void RunEpoch(RunEpoch e)
    {
        _clock = Sender;
        _spikes.Clear();
        _voltages = _watched >= 0 ? new double[e.ToStep - e.FromStep] : null;

        for (long t = e.FromStep; t < e.ToStep; t++)
        {
            Advance(t, (byte)(t - e.FromStep));
            if (_voltages is not null) _voltages[t - e.FromStep] = _v[_watched];
        }

        _awaitingAcks = _touched.Count;
        foreach (int region in _touched)
        {
            var box = _outboxes[region]!;
            _s.Regions[region].Tell(new RegionSpikes(e.FromStep, [.. box.Targets], [.. box.Offsets], [.. box.Weights]));
            box.Targets.Clear();
            box.Offsets.Clear();
            box.Weights.Clear();
        }
        _touched.Clear();
        if (_awaitingAcks == 0) ReportDone();
    }

    // Same four phases, in the same order, as ArraySimulator.Advance.
    void Advance(long t, byte offset)
    {
        double vRest = _p.VRest, vTh = _p.VThreshold;

        for (int i = 0; i < _n; i++)
        {
            if (!_notRefractory[i]) continue;
            double u = _v[i] - vRest, g = _g[i];
            if (u == 0 && g == 0) continue;
            _v[i] = vRest + u * _decayV + g * _gToV;
            _g[i] = g * _decayG;
        }

        _spiked.Clear();
        for (int i = 0; i < _n; i++)
        {
            bool ready = t - _lastSpike[i] >= _refractorySteps[i];
            if (ready && _v[i] > vTh)
            {
                _spiked.Add(i);
                _lastSpike[i] = t;
                ready = false;
            }
            _notRefractory[i] = ready;
        }

        int outSlot = (int)((t + _p.DelaySteps) % _slots) * _n;
        foreach (int i in _spiked)
        {
            int neuron = _members[i];
            _spikes.Add((t, neuron));
            if (_silenced[i]) continue;
            var targets = _brain.TargetsOf(neuron);
            var weights = _brain.WeightsOf(neuron);
            for (int k = 0; k < targets.Length; k++)
            {
                int target = targets[k];
                int region = _s.RegionOf[target];
                if (region == _s.Region)
                {
                    _pending[outSlot + _s.LocalIndex[target]] += weights[k];
                    continue;
                }
                var box = _outboxes[region] ??= new Outbox();
                if (box.Targets.Count == 0) _touched.Add(region);
                box.Targets.Add(_s.LocalIndex[target]);
                box.Offsets.Add(offset);
                box.Weights.Add(weights[k]);
            }
        }

        var inbox = _pending.AsSpan((int)(t % _slots) * _n, _n);
        double w = _p.WeightPerSynapse;
        for (int i = 0; i < _n; i++)
        {
            if (inbox[i] == 0) continue;
            if (_notRefractory[i]) _g[i] += inbox[i] * w;
            inbox[i] = 0;
        }

        foreach (int i in _stimulated)
            if (_notRefractory[i] && Rng.Draw(_s.Seed, _s.Trial, _brain.FlywireIds[_members[i]], t) < _poissonThreshold[i]) _v[i] += _p.PoissonKick;

        foreach (int i in _spiked)
        {
            _v[i] = _p.VReset;
            _g[i] = 0;
        }
    }

    void OnSpikes(RegionSpikes m)
    {
        for (int k = 0; k < m.Targets.Length; k++)
            _pending[(int)((m.Base + m.Offsets[k] + _p.DelaySteps) % _slots) * _n + m.Targets[k]] += m.Weights[k];
        Sender.Tell(new SpikeAck(_s.Region, _idle));
        _idle = false;
    }

    void OnAck(SpikeAck m)
    {
        if (m.Woke) _woken.Add(m.Unit);
        if (--_awaitingAcks == 0) ReportDone();
    }

    void ReportDone()
    {
        bool active = _watched >= 0 || _stimulated.Count > 0 || _pending.AsSpan().ContainsAnyExcept(0);
        for (int i = 0; !active && i < _n; i++) active = _v[i] != _p.VRest || _g[i] != 0;
        _idle = !active;
        _clock.Tell(new EpochDone(_s.Region, [.. _spikes], active, [.. _woken], _voltages));
        _woken.Clear();
    }
}
