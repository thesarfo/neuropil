using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuropil.Core;

// Written by prep/build_meta.py: parallel arrays in brain.bin order, strings interned into tables.
public sealed record BrainMeta(
    int Neurons,
    string[] Regions,
    string[] Classes,
    string[] Types,
    int[] Region,
    [property: JsonPropertyName("class")] int[] Class,
    [property: JsonPropertyName("type")] int[] Type,
    float[] X,
    float[] Y,
    float[] Z)
{
    public static BrainMeta Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<BrainMeta>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>
    /// One unit per brain region, with regions larger than maxSize cut into equal parts so a
    /// few huge regions don't hold everything else up.
    /// </summary>
    public int[] Partition(int maxSize = int.MaxValue)
    {
        var sizes = new int[Regions.Length];
        foreach (int r in Region) sizes[r]++;

        var parts = new int[Regions.Length];
        var firstUnit = new int[Regions.Length];
        int units = 0;
        for (int r = 0; r < sizes.Length; r++)
        {
            parts[r] = Math.Max(1, (int)Math.Ceiling(sizes[r] / (double)maxSize));
            firstUnit[r] = units;
            units += parts[r];
        }

        var seen = new int[Regions.Length];
        var unitOf = new int[Region.Length];
        for (int i = 0; i < Region.Length; i++)
        {
            int r = Region[i];
            unitOf[i] = firstUnit[r] + (int)((long)seen[r]++ * parts[r] / sizes[r]);
        }
        return unitOf;
    }
}
