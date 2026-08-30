using Neuropil.Actors;
using Neuropil.Core;

namespace Neuropil.Core.Tests;

public class ActorSimulatorTests
{
    static Brain RandomBrain(int neurons, int edgesPerNeuron, int seed)
    {
        var rng = new Random(seed);
        var edges = new HashSet<(int, int)>();
        var list = new List<(int, int, short)>();
        for (int pre = 0; pre < neurons; pre++)
            for (int k = 0; k < edgesPerNeuron; k++)
            {
                int post = rng.Next(neurons);
                if (!edges.Add((pre, post))) continue;
                short w = (short)(rng.NextDouble() < 0.7 ? rng.Next(1, 120) : -rng.Next(1, 120));
                list.Add((pre, post, w));
            }
        return Brain.FromEdges(neurons, list);
    }

    static List<(long, int)> ArraySpikes(Brain brain, int[] stimulated, long steps, ulong seed, int trial, int[] silenced)
    {
        var sim = new ArraySimulator(brain, seed: seed);
        sim.Stimulate(stimulated);
        sim.Silence(silenced);
        sim.Reset(trial);
        var spikes = new List<(long, int)>();
        for (long s = 0; s < steps; s++)
            foreach (int i in sim.Advance()) spikes.Add((s, i));
        return spikes;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(3, 7)]
    public async Task Actor_mode_matches_array_mode_spike_for_spike(int graphSeed, int trial)
    {
        var brain = RandomBrain(300, 12, graphSeed);
        int[] stimulated = [.. Enumerable.Range(0, 15)];
        int[] silenced = [20, 21];
        const long steps = 2000; // deliberately not a whole number of epochs

        var expected = ArraySpikes(brain, stimulated, steps, 42, trial, silenced);
        await using var actors = new ActorSimulator(brain, stimulated, seed: 42, trial: trial, silenced: silenced);
        var actual = await actors.RunAsync(steps);

        int downstream = expected.Count(s => s.Item2 >= stimulated.Length);
        Assert.True(downstream > 500, $"only {downstream} downstream spikes; the test network is too quiet");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Running_in_pieces_matches_one_run()
    {
        var brain = RandomBrain(100, 10, 9);
        int[] stimulated = [0, 1, 2, 3, 4];

        await using var once = new ActorSimulator(brain, stimulated, seed: 5);
        var whole = await once.RunAsync(1000);

        await using var pieces = new ActorSimulator(brain, stimulated, seed: 5);
        var parts = new List<(long, int)>();
        foreach (long n in new long[] { 1, 17, 18, 300, 664 })
            parts.AddRange(await pieces.RunAsync(n));

        Assert.Equal(whole, parts);
    }
}
