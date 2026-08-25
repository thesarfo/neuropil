using Neuropil.Core;

namespace Neuropil.Core.Tests;

public class ArraySimulatorTests
{
    // 1/dt: the Poisson draw always succeeds.
    const double Always = 1e4;

    static List<long> SpikeSteps(ArraySimulator sim, int neuron, int steps)
    {
        var at = new List<long>();
        for (int s = 0; s < steps; s++)
            if (sim.Advance().Contains(neuron)) at.Add(s);
        return at;
    }

    [Fact]
    public void Subthreshold_input_follows_the_closed_form_solution()
    {
        var p = LifParams.Default;
        var sim = new ArraySimulator(Brain.FromEdges(1, []), p);
        sim.Inject(0, 10);
        double g0 = 10 * p.WeightPerSynapse;

        for (int s = 0; s < 100; s++) Assert.Empty(sim.Advance());

        double t = 100 * p.Dt;
        double expected = p.VRest + g0 * p.TauSynapse / (p.TauSynapse - p.TauMembrane)
            * (Math.Exp(-t / p.TauSynapse) - Math.Exp(-t / p.TauMembrane));
        Assert.Equal(expected, sim.Voltage[0], 1e-9);
    }

    // Expected spike steps below come from Brian2 2.9 on the same two-neuron circuit.

    [Fact]
    public void Strong_input_fires_after_the_delay_and_refractory_drops_input()
    {
        var sim = new ArraySimulator(Brain.FromEdges(2, [(0, 1, 30000)]));
        sim.Stimulate([0], Always);

        Assert.Equal([20, 44, 68, 92], SpikeSteps(sim, 1, 100));
    }

    [Fact]
    public void Moderate_input_matches_brian2_spike_times()
    {
        var sim = new ArraySimulator(Brain.FromEdges(2, [(0, 1, 397)]));
        sim.Stimulate([0], Always);

        Assert.Equal([26, 56], SpikeSteps(sim, 1, 80));
    }

    [Fact]
    public void Silenced_neurons_spike_but_have_no_effect()
    {
        var sim = new ArraySimulator(Brain.FromEdges(2, [(0, 1, 30000)]));
        sim.Stimulate([0], Always);
        sim.Silence([0]);

        Assert.NotEmpty(SpikeSteps(sim, 0, 100));
        sim.Reset(0);
        Assert.Empty(SpikeSteps(sim, 1, 100));
    }

    [Fact]
    public void Inhibition_cancels_excitation()
    {
        var sim = new ArraySimulator(Brain.FromEdges(3, [(0, 2, 30000), (1, 2, -30000)]));
        sim.Stimulate([0, 1], Always);

        Assert.Empty(SpikeSteps(sim, 2, 200));
    }

    [Fact]
    public void Same_seed_and_trial_repeat_exactly_and_trials_differ()
    {
        var brain = Brain.FromEdges(3, [(0, 2, 40), (1, 2, 40)]);
        List<long> Run(ulong seed, int trial)
        {
            var sim = new ArraySimulator(brain, seed: seed);
            sim.Stimulate([0, 1]);
            sim.Reset(trial);
            return SpikeSteps(sim, 2, 10000);
        }

        Assert.NotEmpty(Run(7, 0));
        Assert.Equal(Run(7, 0), Run(7, 0));
        Assert.NotEqual(Run(7, 0), Run(7, 1));
        Assert.NotEqual(Run(7, 0), Run(8, 0));
    }

    [Fact]
    public void Poisson_input_drives_the_requested_rate()
    {
        var sim = new ArraySimulator(Brain.FromEdges(1, []), seed: 3);
        sim.Stimulate([0], 150);

        int spikes = SpikeSteps(sim, 0, 100_000).Count;
        Assert.InRange(spikes / 10.0, 140, 160);
    }
}
