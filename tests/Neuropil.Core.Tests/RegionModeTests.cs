using Akka.Actor;
using Neuropil.Actors;
using Neuropil.Core;

namespace Neuropil.Core.Tests;

public class RegionModeTests
{
    static int[] RandomRegions(int neurons, int regions, int seed)
    {
        var rng = new Random(seed);
        return [.. Enumerable.Range(0, neurons).Select(_ => rng.Next(regions))];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(23)]
    [InlineData(300)]
    public async Task Region_mode_matches_array_mode_spike_for_spike(int regions)
    {
        var brain = ActorSimulatorTests.RandomBrain(300, 12, 2);
        int[] stimulated = [.. Enumerable.Range(0, 15)];
        int[] silenced = [20, 21];
        const long steps = 2000;

        var array = new ArraySimulator(brain, seed: 42);
        array.Stimulate(stimulated);
        array.Silence(silenced);
        array.Reset(3);
        var expected = new List<(long, int)>();
        for (long s = 0; s < steps; s++)
            foreach (int i in array.Advance()) expected.Add((s, i));

        await using var actors = new ActorSimulator(brain, stimulated, seed: 42, trial: 3, silenced: silenced,
            regionOf: RandomRegions(brain.NeuronCount, regions, 7));
        var actual = await actors.RunAsync(steps);

        Assert.True(expected.Count(s => s.Item2 >= 15) > 500, "the test network is too quiet");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Stimulation_and_watching_work_across_regions()
    {
        var brain = ActorSimulatorTests.RandomBrain(200, 12, 4);
        int[] first = [0, 1, 2, 3, 4, 5], second = [6, 7, 8, 9];
        const int watched = 60;

        var array = new ArraySimulator(brain, seed: 11);
        await using var actors = new ActorSimulator(brain, [], seed: 11, regionOf: RandomRegions(200, 8, 1));
        var inbox = Inbox.Create(actors.System);
        actors.Clock.Tell(new Subscribe(inbox.Receiver));
        actors.Clock.Tell(new Watch(watched));

        var expected = new List<(long, int)>();
        var expectedVolts = new List<double>();
        var actual = new List<(long, int)>();
        void Both(int[] neurons, double rate)
        {
            array.Stimulate(neurons, rate);
            actors.Stimulate(neurons, rate);
        }

        Both(first, 150);
        foreach (var (steps, change) in new (long, Action)[] { (500, () => Both(second, 300)), (700, () => Both(first, 0)), (800, () => { }) })
        {
            for (long s = 0; s < steps; s++)
            {
                long at = array.Step;
                foreach (int i in array.Advance()) expected.Add((at, i));
                expectedVolts.Add(array.Voltage[watched]);
            }
            actual.AddRange(await actors.RunAsync(steps));
            change();
        }

        var volts = new List<double>();
        while (volts.Count < expectedVolts.Count)
            volts.AddRange(((EpochSpikes)await inbox.ReceiveAsync(TimeSpan.FromSeconds(5))).Voltages!);

        Assert.True(expected.Count > 500, $"only {expected.Count} spikes");
        Assert.Equal(expected, actual);
        Assert.Equal(expectedVolts, volts);
    }
}
