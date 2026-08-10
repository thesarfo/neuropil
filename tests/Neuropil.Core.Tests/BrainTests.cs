using Neuropil.Core;

namespace Neuropil.Core.Tests;

public class BrainTests
{
    [Fact]
    public void Edges_are_grouped_by_presynaptic_neuron()
    {
        var brain = Brain.FromEdges(3, [(2, 0, -4), (0, 2, 5), (0, 1, 3)]);

        Assert.Equal([1, 2], brain.TargetsOf(0).ToArray());
        Assert.Equal([3, 5], brain.WeightsOf(0).ToArray());
        Assert.Empty(brain.TargetsOf(1).ToArray());
        Assert.Equal([0], brain.TargetsOf(2).ToArray());
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var brain = Brain.FromEdges(3, [(2, 0, -4), (0, 2, 5), (0, 1, 3)], [100, 200, 300]);
        string path = Path.GetTempFileName();
        try
        {
            brain.Save(path);
            var loaded = Brain.Load(path);

            Assert.Equal(brain.FlywireIds, loaded.FlywireIds);
            Assert.Equal(brain.RowPtr, loaded.RowPtr);
            Assert.Equal(brain.Targets, loaded.Targets);
            Assert.Equal(brain.Weights, loaded.Weights);
            Assert.Equal(1, loaded.IndexOf(200));
            Assert.Equal(-1, loaded.IndexOf(999));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
