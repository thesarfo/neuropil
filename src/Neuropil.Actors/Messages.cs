using Akka.Actor;

namespace Neuropil.Actors;

// Time moves in epochs of DelaySteps. A spike sent in one epoch can't arrive before the next,
// so units run a whole epoch independently. A unit is one neuron, or one region holding many.

public sealed record RunEpoch(long FromStep, long ToStep);

public sealed record SpikeTrain(short Weight, long[] Steps);

// Every spike a region sends another region in one epoch. Arrival step is Base + Offsets[k] + delay;
// Targets are indexes local to the receiving region.
public sealed record RegionSpikes(long Base, int[] Targets, byte[] Offsets, short[] Weights);

public sealed record SpikeAck(int Unit, bool Woke);

// Sent only once every SpikeTrain is acknowledged, so a complete set means all spikes are delivered.
public sealed record EpochDone(int Unit, IReadOnlyList<(long Step, int Neuron)> Spikes, bool Active, int[] Woken, double[]? Voltages = null);

public sealed record Run(long Steps);

// Takes effect at the next epoch boundary. A rate of zero switches stimulation off.
public sealed record Stimulate(IReadOnlyList<int> Neurons, double RateHz);

public sealed record SetStimulus(int Neuron, ulong PoissonThreshold);

// A silenced neuron still fires but nobody hears it. Takes effect at the next epoch boundary.
public sealed record Silence(IReadOnlyList<int> Neurons, bool Silenced);

public sealed record SetSilenced(int Neuron, bool Silenced);

public sealed record Subscribe(IActorRef Subscriber);

public sealed record EpochSpikes(
    long FromStep,
    long ToStep,
    IReadOnlyList<(long Step, int Neuron)> Spikes,
    int? Watched = null,
    double[]? Voltages = null);

// Record one neuron's voltage at every step, or none.
public sealed record Watch(int? Neuron);

public sealed record SetWatched(int Neuron, bool Watched);

public sealed record RunResult(IReadOnlyList<(long Step, int Neuron)> Spikes);
