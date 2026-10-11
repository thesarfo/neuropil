using System.Text.Json;
using Neuropil.Actors;
using Neuropil.Core;

namespace Neuropil.Server;

public sealed record Stimulus(string Name, CircuitInfo Info, IReadOnlyList<int> Sensory);

/// <summary>The whole brain with its positions and regions, plus every preset as a stimulus that can be applied to it.</summary>
public sealed class Atlas
{
    public const string Name = "brain";
    public const string FlyName = "fly";
    const int MaxRegionSize = 500;

    public required Brain Brain { get; init; }
    public required BrainMeta Meta { get; init; }
    public required string MetaPath { get; init; }
    public required int[] UnitOf { get; init; }
    public required IReadOnlyList<Stimulus> Stimuli { get; init; }
    public required IReadOnlyDictionary<int, string> Labels { get; init; }

    /// <summary>Sensors and motors for the embodied fly; null when presets/body.json is missing.</summary>
    public BodyPlan? Body { get; init; }

    public int Actors => UnitOf.Max() + 1;

    /// <summary>One character per neuron, '1' where its outgoing synapses are inhibitory on balance.</summary>
    public string Inhibitory => string.Create(Brain.NeuronCount, Brain, static (chars, brain) =>
    {
        for (int i = 0; i < chars.Length; i++)
        {
            long sum = 0;
            foreach (short w in brain.WeightsOf(i)) sum += w;
            chars[i] = sum < 0 ? '1' : '0';
        }
    });

    public static Atlas? Load(string brainPath, string metaPath, string presetsDir)
    {
        if (!File.Exists(brainPath) || !File.Exists(metaPath)) return null;
        var brain = Brain.Load(brainPath);
        var meta = BrainMeta.Load(metaPath);
        if (meta.Neurons != brain.NeuronCount) throw new InvalidDataException($"{metaPath} describes {meta.Neurons} neurons but {brainPath} has {brain.NeuronCount}");

        var info = Presets.ReadInfo(presetsDir);
        var stimuli = new List<Stimulus>();
        var labels = new Dictionary<int, string>();
        foreach (var (name, about) in info)
        {
            string preset = Path.Combine(presetsDir, name + ".txt");
            if (!File.Exists(preset)) continue;
            stimuli.Add(new Stimulus(name, about, Presets.ReadNeurons(preset, brain)));
            foreach (var (index, label) in Presets.ReadLabels(Path.Combine(presetsDir, name + ".outputs.txt"), brain)) labels[index] = label;
        }

        return new Atlas
        {
            Brain = brain,
            Meta = meta,
            MetaPath = metaPath,
            UnitOf = meta.Partition(MaxRegionSize),
            Stimuli = stimuli,
            Labels = labels,
            Body = ReadBody(Path.Combine(presetsDir, "body.json"), stimuli, labels),
        };
    }

    sealed record SensorFile(string Preset, double MaxRateHz);

    sealed record BodyFile(Dictionary<string, SensorFile> Sensors, Dictionary<string, string[]> Motors);

    static BodyPlan? ReadBody(string path, List<Stimulus> stimuli, Dictionary<int, string> labels)
    {
        if (!File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<BodyFile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var neuronOf = labels.ToDictionary(kv => kv.Value, kv => kv.Key);

        var sensors = new Dictionary<string, (IReadOnlyList<int>, double)>();
        foreach (var (name, sensor) in file.Sensors)
        {
            var stimulus = stimuli.FirstOrDefault(s => s.Name == sensor.Preset)
                ?? throw new InvalidDataException($"{path}: sensor \"{name}\" names unknown preset \"{sensor.Preset}\"");
            sensors[name] = (stimulus.Sensory, sensor.MaxRateHz);
        }

        var motors = new Dictionary<string, IReadOnlyList<int>>();
        foreach (var (name, neurons) in file.Motors)
        {
            motors[name] = [.. neurons.Select(label => neuronOf.TryGetValue(label, out int neuron)
                ? neuron
                : throw new InvalidDataException($"{path}: motor \"{name}\" names unknown neuron \"{label}\""))];
        }
        return new BodyPlan(sensors, motors);
    }

    /// <summary>A neuron's strongest connections in each direction, as (neuron, synapses) pairs.</summary>
    public (List<int[]> Inputs, List<int[]> Outputs) Neighbours(int neuron, int top = 8)
    {
        var inputs = new List<int[]>();
        var targets = Brain.Targets;
        for (int pre = 0; pre < Brain.NeuronCount; pre++)
            for (long k = Brain.RowPtr[pre]; k < Brain.RowPtr[pre + 1]; k++)
                if (targets[k] == neuron) inputs.Add([pre, Brain.Weights[k]]);

        var outputs = new List<int[]>();
        var outTargets = Brain.TargetsOf(neuron);
        var outWeights = Brain.WeightsOf(neuron);
        for (int k = 0; k < outTargets.Length; k++) outputs.Add([outTargets[k], outWeights[k]]);

        List<int[]> Strongest(List<int[]> edges) => [.. edges.OrderByDescending(e => Math.Abs(e[1])).Take(top)];
        return (Strongest(inputs), Strongest(outputs));
    }
}

public static class Presets
{
    public static List<int> ReadNeurons(string path, Brain brain) =>
        [.. File.ReadLines(path)
            .Where(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'))
            .Select(l => brain.IndexOf(long.Parse(l)))
            .Where(i => i >= 0)];

    public static Dictionary<int, string> ReadLabels(string path, Brain brain)
    {
        var labels = new Dictionary<int, string>();
        if (!File.Exists(path)) return labels;
        foreach (string line in File.ReadLines(path))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && long.TryParse(parts[0], out long id) && brain.IndexOf(id) is >= 0 and int index)
                labels[index] = parts[1];
        }
        return labels;
    }

    public static Dictionary<string, CircuitInfo> ReadInfo(string presetsDir)
    {
        string path = Path.Combine(presetsDir, "circuits.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, CircuitInfo>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            : [];
    }
}
