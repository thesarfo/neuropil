namespace Neuropil.Core;

// Follows Brian2's schedule each step: state update, threshold, synapses, reset.
// Refractory neurons are frozen and drop all input, as Brian2 2.9 does.
public sealed class ArraySimulator
{
    readonly Brain _brain;
    readonly LifParams _p;
    readonly ulong _seed;

    readonly double _decayV, _decayG, _gToV;

    readonly double[] _v, _g;
    readonly long[] _lastSpike;
    readonly bool[] _notRefractory;
    readonly int[] _refractorySteps;
    readonly bool[] _silenced;

    // Summed as integer synapse counts so arrival order can't change the result.
    readonly int[][] _pending;

    readonly ulong[] _poissonThreshold;
    readonly SortedSet<int> _stimulated = [];
    readonly List<int> _spiked = [];

    public long Step { get; private set; }
    public int Trial { get; private set; }

    public ArraySimulator(Brain brain, LifParams? p = null, ulong seed = 0)
    {
        _brain = brain;
        _p = p ?? LifParams.Default;
        _seed = seed;

        (_decayV, _decayG, _gToV) = (_p.DecayV, _p.DecayG, _p.GToV);

        int n = brain.NeuronCount;
        _v = new double[n];
        _g = new double[n];
        _lastSpike = new long[n];
        _notRefractory = new bool[n];
        _refractorySteps = new int[n];
        _silenced = new bool[n];
        _poissonThreshold = new ulong[n];
        _pending = new int[_p.DelaySteps + 1][];
        for (int i = 0; i < _pending.Length; i++) _pending[i] = new int[n];
        Array.Fill(_refractorySteps, _p.RefractorySteps);
        Reset(0);
    }

    public ReadOnlySpan<double> Voltage => _v;

    // Like the reference, stimulated neurons have no refractory period. A rate of zero switches it off.
    public void Stimulate(IEnumerable<int> neurons, double? rateHz = null)
    {
        ulong threshold = rateHz is <= 0 ? 0 : _p.PoissonThreshold(rateHz);
        foreach (int i in neurons)
        {
            _poissonThreshold[i] = threshold;
            _refractorySteps[i] = threshold > 0 ? 0 : _p.RefractorySteps;
            if (threshold > 0) _stimulated.Add(i); else _stimulated.Remove(i);
        }
    }

    public void Silence(IEnumerable<int> neurons, bool silenced = true)
    {
        foreach (int i in neurons) _silenced[i] = silenced;
    }

    public void Inject(int neuron, int synapses) => _g[neuron] += synapses * _p.WeightPerSynapse;

    public void Reset(int trial)
    {
        Trial = trial;
        Step = 0;
        Array.Fill(_v, _p.VRest);
        Array.Clear(_g);
        Array.Fill(_lastSpike, long.MinValue / 2);
        Array.Fill(_notRefractory, true);
        foreach (var slot in _pending) Array.Clear(slot);
    }

    public IReadOnlyList<int> Advance()
    {
        long t = Step;
        int n = _brain.NeuronCount;
        double vRest = _p.VRest, vTh = _p.VThreshold;

        for (int i = 0; i < n; i++)
        {
            if (!_notRefractory[i]) continue;
            double u = _v[i] - vRest, g = _g[i];
            if (u == 0 && g == 0) continue;
            _v[i] = vRest + u * _decayV + g * _gToV;
            _g[i] = g * _decayG;
        }

        _spiked.Clear();
        for (int i = 0; i < n; i++)
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

        var outbox = _pending[(t + _p.DelaySteps) % _pending.Length];
        foreach (int i in _spiked)
        {
            if (_silenced[i]) continue;
            var targets = _brain.TargetsOf(i);
            var weights = _brain.WeightsOf(i);
            for (int k = 0; k < targets.Length; k++) outbox[targets[k]] += weights[k];
        }

        var inbox = _pending[t % _pending.Length];
        double w = _p.WeightPerSynapse;
        for (int i = 0; i < n; i++)
        {
            if (inbox[i] == 0) continue;
            if (_notRefractory[i]) _g[i] += inbox[i] * w;
            inbox[i] = 0;
        }

        foreach (int i in _stimulated)
            if (_notRefractory[i] && Rng.Draw(_seed, Trial, _brain.FlywireIds[i], t) < _poissonThreshold[i]) _v[i] += _p.PoissonKick;

        foreach (int i in _spiked)
        {
            _v[i] = _p.VReset;
            _g[i] = 0;
        }

        Step = t + 1;
        return _spiked;
    }
}
