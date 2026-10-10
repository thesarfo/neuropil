using Akka.Actor;
using Neuropil.Core;

namespace Neuropil.Actors;

public sealed class NeuronActor : ReceiveActor
{
    public sealed record Setup(
        Brain Brain,
        int Index,
        IActorRef[] Neurons,
        LifParams P,
        ulong Seed,
        int Trial,
        ulong PoissonThreshold,
        bool Silenced);

    readonly Setup _s;
    readonly LifParams _p;
    readonly long _flywireId;
    readonly double _decayV, _decayG, _gToV;
    int _refractorySteps;
    ulong _poissonThreshold;

    double _v, _g;
    long _lastSpike = long.MinValue / 2;

    // Indexed by arrival step mod two epochs; arrivals always fall in the current or next one.
    readonly int[] _pending;

    bool _idle, _watched;
    double[]? _voltages;
    int _awaitingAcks;
    long[] _spikes = [];
    readonly List<int> _woken = [];
    IActorRef _clock = ActorRefs.Nobody;

    public NeuronActor(Setup s)
    {
        _s = s;
        _p = s.P;
        _flywireId = s.Brain.FlywireIds[s.Index];
        (_decayV, _decayG, _gToV) = (_p.DecayV, _p.DecayG, _p.GToV);
        SetStimulus(s.PoissonThreshold);
        _v = _p.VRest;
        _pending = new int[2 * _p.DelaySteps];

        Receive<RunEpoch>(RunEpoch);
        Receive<SpikeTrain>(OnSpikes);
        Receive<SpikeAck>(OnAck);
        Receive<SetStimulus>(m => SetStimulus(m.PoissonThreshold));
        Receive<SetWatched>(m => _watched = m.Watched);
    }

    void SetStimulus(ulong threshold)
    {
        _poissonThreshold = threshold;
        _refractorySteps = threshold > 0 ? 0 : _p.RefractorySteps;
    }

    void RunEpoch(RunEpoch e)
    {
        _clock = Sender;
        var spikes = new List<long>();
        _voltages = _watched ? new double[e.ToStep - e.FromStep] : null;
        for (long t = e.FromStep; t < e.ToStep; t++)
        {
            if (Advance(t)) spikes.Add(t);
            if (_voltages is not null) _voltages[t - e.FromStep] = _v;
        }
        _spikes = [.. spikes];

        var targets = _s.Brain.TargetsOf(_s.Index);
        if (_spikes.Length == 0 || _s.Silenced || targets.Length == 0)
        {
            ReportDone();
            return;
        }

        var weights = _s.Brain.WeightsOf(_s.Index);
        _awaitingAcks = targets.Length;
        for (int k = 0; k < targets.Length; k++)
            _s.Neurons[targets[k]].Tell(new SpikeTrain(weights[k], _spikes));
    }

    bool Advance(long t)
    {
        // Brian2 decides whether to integrate from the previous step's threshold check.
        bool wasNotRefractory = _lastSpike != t - 1 && t - 1 - _lastSpike >= _refractorySteps;
        if (wasNotRefractory)
        {
            double u = _v - _p.VRest;
            if (u != 0 || _g != 0)
            {
                _v = _p.VRest + u * _decayV + _g * _gToV;
                _g *= _decayG;
            }
        }

        bool notRefractory = t - _lastSpike >= _refractorySteps;
        bool spiked = notRefractory && _v > _p.VThreshold;
        if (spiked)
        {
            _lastSpike = t;
            notRefractory = false;
        }

        int slot = (int)(t % _pending.Length);
        if (_pending[slot] != 0)
        {
            if (notRefractory) _g += _pending[slot] * _p.WeightPerSynapse;
            _pending[slot] = 0;
        }

        if (notRefractory && _poissonThreshold > 0 && Rng.Draw(_s.Seed, _s.Trial, _flywireId, t) < _poissonThreshold)
            _v += _p.PoissonKick;

        if (spiked)
        {
            _v = _p.VReset;
            _g = 0;
        }
        return spiked;
    }

    void OnSpikes(SpikeTrain m)
    {
        foreach (long step in m.Steps)
            _pending[(int)((step + _p.DelaySteps) % _pending.Length)] += m.Weight;
        Sender.Tell(new SpikeAck(_s.Index, _idle));
        _idle = false;
    }

    void OnAck(SpikeAck m)
    {
        if (m.Woke) _woken.Add(m.Neuron);
        if (--_awaitingAcks == 0) ReportDone();
    }

    void ReportDone()
    {
        bool active = _watched || _poissonThreshold > 0 || _v != _p.VRest || _g != 0 || Array.Exists(_pending, x => x != 0);
        _idle = !active;
        _clock.Tell(new EpochDone(_s.Index, _spikes, active, [.. _woken], _voltages));
        _woken.Clear();
    }
}
