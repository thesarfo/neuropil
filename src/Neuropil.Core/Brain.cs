using System.Runtime.InteropServices;
using System.Text;

namespace Neuropil.Core;

// Compressed sparse rows; weights are signed synapse counts. File layout: prep/build_brain.py.
public sealed class Brain
{
    static readonly byte[] Magic = "NPIL"u8.ToArray();
    const int Version = 1;

    public long[] FlywireIds { get; }
    public long[] RowPtr { get; }
    public int[] Targets { get; }
    public short[] Weights { get; }

    public int NeuronCount => FlywireIds.Length;
    public long EdgeCount => Targets.LongLength;

    Dictionary<long, int>? _indexById;

    public Brain(long[] flywireIds, long[] rowPtr, int[] targets, short[] weights)
    {
        if (rowPtr.Length != flywireIds.Length + 1) throw new ArgumentException("rowPtr must have N+1 entries");
        if (targets.Length != weights.Length || rowPtr[^1] != targets.Length) throw new ArgumentException("edge arrays disagree with rowPtr");
        FlywireIds = flywireIds;
        RowPtr = rowPtr;
        Targets = targets;
        Weights = weights;
    }

    public static Brain FromEdges(int neurons, IEnumerable<(int Pre, int Post, short Weight)> edges, long[]? flywireIds = null)
    {
        var sorted = edges.OrderBy(e => e.Pre).ThenBy(e => e.Post).ToArray();
        var rowPtr = new long[neurons + 1];
        foreach (var e in sorted) rowPtr[e.Pre + 1]++;
        for (int i = 0; i < neurons; i++) rowPtr[i + 1] += rowPtr[i];
        return new Brain(
            flywireIds ?? Enumerable.Range(0, neurons).Select(i => (long)i).ToArray(),
            rowPtr,
            sorted.Select(e => e.Post).ToArray(),
            sorted.Select(e => e.Weight).ToArray());
    }

    public Brain Subset(IReadOnlyList<int> neurons)
    {
        var newIndex = new Dictionary<int, int>();
        for (int i = 0; i < neurons.Count; i++) newIndex[neurons[i]] = i;

        var edges = new List<(int, int, short)>();
        for (int i = 0; i < neurons.Count; i++)
        {
            var targets = TargetsOf(neurons[i]);
            var weights = WeightsOf(neurons[i]);
            for (int k = 0; k < targets.Length; k++)
                if (newIndex.TryGetValue(targets[k], out int post)) edges.Add((i, post, weights[k]));
        }
        return FromEdges(neurons.Count, edges, [.. neurons.Select(i => FlywireIds[i])]);
    }

    public static Brain Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var r = new BinaryReader(stream);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException($"{path} is not a brain file");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"{path} has version {version}, expected {Version}");
        int n = r.ReadInt32();
        long e = r.ReadInt64();

        var ids = new long[n];
        var rowPtr = new long[n + 1];
        var targets = new int[e];
        var weights = new short[e];
        stream.ReadExactly(MemoryMarshal.AsBytes(ids.AsSpan()));
        stream.ReadExactly(MemoryMarshal.AsBytes(rowPtr.AsSpan()));
        stream.ReadExactly(MemoryMarshal.AsBytes(targets.AsSpan()));
        stream.ReadExactly(MemoryMarshal.AsBytes(weights.AsSpan()));
        return new Brain(ids, rowPtr, targets, weights);
    }

    public void Save(string path)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        w.Write(Magic);
        w.Write(Version);
        w.Write(NeuronCount);
        w.Write(EdgeCount);
        w.Flush();
        stream.Write(MemoryMarshal.AsBytes(FlywireIds.AsSpan()));
        stream.Write(MemoryMarshal.AsBytes(RowPtr.AsSpan()));
        stream.Write(MemoryMarshal.AsBytes(Targets.AsSpan()));
        stream.Write(MemoryMarshal.AsBytes(Weights.AsSpan()));
    }

    public int IndexOf(long flywireId)
    {
        _indexById ??= FlywireIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return _indexById.TryGetValue(flywireId, out int i) ? i : -1;
    }

    public ReadOnlySpan<int> TargetsOf(int neuron) => Targets.AsSpan(Range(neuron));
    public ReadOnlySpan<short> WeightsOf(int neuron) => Weights.AsSpan(Range(neuron));

    Range Range(int neuron) => (int)RowPtr[neuron]..(int)RowPtr[neuron + 1];
}
