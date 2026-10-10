using Akka.Actor;

namespace Neuropil.Actors;

// Time moves in epochs of DelaySteps. A spike sent in one epoch can't arrive before the next,
// so neurons run a whole epoch independently.

public sealed record RunEpoch(long FromStep, long ToStep);

public sealed record SpikeTrain(short Weight, long[] Steps);

public sealed record SpikeAck(int Neuron, bool Woke);

// Sent only once every SpikeTrain is acknowledged, so a complete set means all spikes are delivered.
public sealed record EpochDone(int Neuron, long[] Spikes, bool Active, int[] Woken, double[]? Voltages = null);

public sealed record Run(long Steps);

// Takes effect at the next epoch boundary. A rate of zero switches stimulation off.
public sealed record Stimulate(IReadOnlyList<int> Neurons, double RateHz);

public sealed record SetStimulus(ulong PoissonThreshold);

public sealed record Subscribe(IActorRef Subscriber);

public sealed record EpochSpikes(
    long FromStep,
    long ToStep,
    IReadOnlyList<(long Step, int Neuron)> Spikes,
    int? Watched = null,
    double[]? Voltages = null);

// Record one neuron's voltage at every step, or none.
public sealed record Watch(int? Neuron);

public sealed record SetWatched(bool Watched);

public sealed record RunResult(IReadOnlyList<(long Step, int Neuron)> Spikes);
