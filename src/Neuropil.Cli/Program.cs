using System.Diagnostics;
using System.Globalization;
using Neuropil.Actors;
using Neuropil.Core;

const string Usage = """
    usage:
      neuropil info    <brain.bin>
      neuropil run     <brain.bin> <preset.txt> [--trials 30] [--ms 1000] [--rate 150] [--seed 1] [--out rates.csv]
      neuropil trace   <brain.bin> <preset.txt> [--ms 100] [--rate 150] [--seed 1] [--out spikes.csv]
      neuropil circuit <brain.bin> <preset.txt> [--size 500] [--out circuit.bin]
      neuropil compare <brain.bin> <preset.txt> [--ms 1000] [--rate 150] [--seed 1] [--trial 0]

    circuit keeps the preset's neurons plus the most active neurons of a one-second array run.
    compare runs array mode and one-actor-per-neuron mode and checks they spike identically.
    """;

if (args.Length < 2 || (args[0] != "info" && args.Length < 3))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var clock = Stopwatch.StartNew();
var brain = Brain.Load(args[1]);
Console.WriteLine($"loaded {brain.NeuronCount:N0} neurons, {brain.EdgeCount:N0} edges, "
    + $"{brain.Weights.Sum(w => (long)Math.Abs(w)):N0} synapses in {clock.Elapsed.TotalSeconds:F1}s");
if (args[0] == "info") return 0;

var opts = Options(args[3..]);
var ids = File.ReadLines(args[2])
    .Where(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'))
    .Select(long.Parse)
    .ToList();
var stimulated = ids.Select(brain.IndexOf).Where(i => i >= 0).ToList();
if (stimulated.Count < ids.Count)
    Console.WriteLine($"skipping {ids.Count - stimulated.Count} IDs not in this brain");

double? rate = opts.ContainsKey("rate") ? Double("rate", 150) : null;
ulong seed = (ulong)Double("seed", 1);
long steps = (long)Math.Round(Double("ms", args[0] == "trace" ? 100 : 1000) / LifParams.Default.Dt);

switch (args[0])
{
    case "trace":
    {
        var sim = new ArraySimulator(brain, seed: seed);
        sim.Stimulate(stimulated, rate);
        string path = opts.GetValueOrDefault("out", "spikes.csv");
        using var w = new StreamWriter(path);
        w.WriteLine("step,flywire_id");
        for (long s = 0; s < steps; s++)
            foreach (int i in sim.Advance()) w.WriteLine($"{s},{brain.FlywireIds[i]}");
        Console.WriteLine($"wrote {path}");
        return 0;
    }

    case "run":
    {
        var spec = new ExperimentSpec(stimulated, steps * LifParams.Default.Dt, (int)Double("trials", 30), seed, rate);
        clock.Restart();
        var result = Experiment.Run(brain, spec);
        Console.WriteLine($"{spec.Trials} trials x {spec.DurationMs} ms in {clock.Elapsed.TotalSeconds:F1}s");
        string path = opts.GetValueOrDefault("out", "rates.csv");
        using (var w = new StreamWriter(path)) result.WriteRatesCsv(w);
        Console.WriteLine($"wrote {path}");
        return 0;
    }

    case "circuit":
    {
        int size = (int)Double("size", 500);
        var counts = Experiment.Run(brain, new ExperimentSpec(stimulated, Trials: 1, Seed: seed, RateHz: rate)).CountsByTrial[0];
        var keep = stimulated
            .Concat(Enumerable.Range(0, brain.NeuronCount)
                .Where(i => counts[i] > 0 && !stimulated.Contains(i))
                .OrderByDescending(i => counts[i]))
            .Take(size)
            .ToList();
        var circuit = brain.Subset(keep);
        string path = opts.GetValueOrDefault("out", "circuit.bin");
        circuit.Save(path);
        Console.WriteLine($"wrote {path}: {circuit.NeuronCount} neurons, {circuit.EdgeCount:N0} edges");
        return 0;
    }

    case "compare":
    {
        int trial = (int)Double("trial", 0);

        clock.Restart();
        var array = new ArraySimulator(brain, seed: seed);
        array.Stimulate(stimulated, rate);
        array.Reset(trial);
        var expected = new List<(long Step, int Neuron)>();
        for (long s = 0; s < steps; s++)
            foreach (int i in array.Advance()) expected.Add((s, i));
        var arrayTime = clock.Elapsed;

        clock.Restart();
        await using var actors = new ActorSimulator(brain, stimulated, seed: seed, trial: trial, rateHz: rate);
        var actual = await actors.RunAsync(steps);
        var actorTime = clock.Elapsed;

        int firstDiff = Enumerable.Range(0, Math.Min(expected.Count, actual.Count)).FirstOrDefault(k => expected[k] != actual[k], -1);
        bool same = expected.SequenceEqual(actual);
        Console.WriteLine($"array:  {expected.Count:N0} spikes from {expected.Select(s => s.Neuron).Distinct().Count()} neurons in {arrayTime.TotalSeconds:F2}s");
        Console.WriteLine($"actors: {actual.Count:N0} spikes from {actual.Select(s => s.Neuron).Distinct().Count()} neurons in {actorTime.TotalSeconds:F2}s");
        Console.WriteLine(same ? "identical spike trains" : $"DIFFERENT, first at spike #{firstDiff}");
        return same ? 0 : 1;
    }
}

Console.Error.WriteLine(Usage);
return 2;

static Dictionary<string, string> Options(string[] a) =>
    Enumerable.Range(0, a.Length / 2).ToDictionary(i => a[2 * i].TrimStart('-'), i => a[2 * i + 1]);

double Double(string key, double fallback) =>
    opts.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
