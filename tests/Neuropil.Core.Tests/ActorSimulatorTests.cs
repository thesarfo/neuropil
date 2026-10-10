using Akka.Actor;
using Neuropil.Actors;
using Neuropil.Core;

namespace Neuropil.Core.Tests;

public class ActorSimulatorTests
{
    internal static Brain RandomBrain(int neurons, int edgesPerNeuron, int seed)
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

    [Fact]
    public async Task Stimulation_changed_mid_run_matches_array_mode()
    {
        var brain = RandomBrain(200, 12, 4);
        int[] first = [0, 1, 2, 3, 4, 5], second = [6, 7, 8, 9];
        (long Steps, Action<Action<int[], double>> Change)[] script =
        [
            (0, set => set(first, 150)),
            (500, set => set(second, 300)),
            (700, set => set(first, 0)),
            (800, _ => { }),
        ];

        var array = new ArraySimulator(brain, seed: 11);
        var expected = new List<(long, int)>();
        await using var actors = new ActorSimulator(brain, [], seed: 11);
        var actual = new List<(long, int)>();

        foreach (var (steps, change) in script)
        {
            for (long s = 0; s < steps; s++)
            {
                long at = array.Step;
                foreach (int i in array.Advance()) expected.Add((at, i));
            }
            actual.AddRange(await actors.RunAsync(steps));
            change((neurons, rate) => { array.Stimulate(neurons, rate); actors.Stimulate(neurons, rate); });
        }

        Assert.True(expected.Count > 500, $"only {expected.Count} spikes");
        Assert.Contains(expected, s => s.Item1 > 1300 && s.Item2 >= 6);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Subscriber_receives_every_spike_epoch_by_epoch()
    {
        var brain = RandomBrain(100, 10, 9);
        await using var actors = new ActorSimulator(brain, [0, 1, 2, 3, 4], seed: 5);
        var inbox = Akka.Actor.Inbox.Create(actors.System);
        actors.Clock.Tell(new Subscribe(inbox.Receiver));

        var whole = await actors.RunAsync(100);

        var streamed = new List<(long, int)>();
        long covered = 0;
        while (covered < 100)
        {
            var epoch = (EpochSpikes)await inbox.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(covered, epoch.FromStep);
            covered = epoch.ToStep;
            streamed.AddRange(epoch.Spikes);
        }
        Assert.NotEmpty(whole);
        Assert.Equal(whole, streamed);
    }

    [Fact]
    public async Task Watched_neuron_reports_the_same_voltage_as_array_mode()
    {
        var brain = RandomBrain(100, 10, 9);
        int[] stimulated = [0, 1, 2, 3, 4];
        const int watched = 40;
        await using var actors = new ActorSimulator(brain, stimulated, seed: 5);
        var inbox = Inbox.Create(actors.System);
        actors.Clock.Tell(new Subscribe(inbox.Receiver));
        actors.Clock.Tell(new Watch(watched));
        await actors.RunAsync(400);

        var array = new ArraySimulator(brain, seed: 5);
        array.Stimulate(stimulated);
        var expected = new List<double>();
        for (int s = 0; s < 400; s++)
        {
            array.Advance();
            expected.Add(array.Voltage[watched]);
        }

        var actual = new List<double>();
        while (actual.Count < 400)
        {
            var epoch = (EpochSpikes)await inbox.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(watched, epoch.Watched);
            actual.AddRange(epoch.Voltages!);
        }
        Assert.Contains(expected, v => v != LifParams.Default.VRest);
        Assert.Equal(expected, actual);
    }
}
