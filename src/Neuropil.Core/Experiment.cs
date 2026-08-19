namespace Neuropil.Core;

public sealed record ExperimentSpec(
    IReadOnlyList<int> Stimulated,
    double DurationMs = 1000,
    int Trials = 30,
    ulong Seed = 1,
    double? RateHz = null,
    IReadOnlyList<int>? Silenced = null);

public sealed class ExperimentResult(Brain brain, int[][] countsByTrial, double durationMs)
{
    public Brain Brain { get; } = brain;
    public int[][] CountsByTrial { get; } = countsByTrial;
    public double DurationMs { get; } = durationMs;

    public double MeanRate(int neuron) => CountsByTrial.Average(c => c[neuron]) * 1000 / DurationMs;

    public double StdRate(int neuron)
    {
        double mean = CountsByTrial.Average(c => c[neuron]);
        double variance = CountsByTrial.Average(c => (c[neuron] - mean) * (c[neuron] - mean));
        return Math.Sqrt(variance) * 1000 / DurationMs;
    }

    public void WriteRatesCsv(TextWriter w)
    {
        w.WriteLine("flywire_id,rate_hz,std_hz");
        var active = Enumerable.Range(0, Brain.NeuronCount)
            .Where(i => CountsByTrial.Any(c => c[i] > 0))
            .Select(i => (Id: Brain.FlywireIds[i], Rate: MeanRate(i), Std: StdRate(i)))
            .OrderByDescending(x => x.Rate);
        foreach (var (id, rate, std) in active)
            w.WriteLine(FormattableString.Invariant($"{id},{rate},{std}"));
    }
}

public static class Experiment
{
    public static ExperimentResult Run(Brain brain, ExperimentSpec spec, LifParams? p = null)
    {
        p ??= LifParams.Default;
        long steps = (long)Math.Round(spec.DurationMs / p.Dt);
        var counts = new int[spec.Trials][];

        Parallel.For(0, spec.Trials, trial =>
        {
            var sim = new ArraySimulator(brain, p, spec.Seed);
            sim.Stimulate(spec.Stimulated, spec.RateHz);
            if (spec.Silenced is { } silenced) sim.Silence(silenced);
            sim.Reset(trial);

            var c = new int[brain.NeuronCount];
            for (long s = 0; s < steps; s++)
                foreach (int i in sim.Advance()) c[i]++;
            counts[trial] = c;
        });

        return new ExperimentResult(brain, counts, spec.DurationMs);
    }
}
