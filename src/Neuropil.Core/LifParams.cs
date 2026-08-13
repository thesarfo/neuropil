namespace Neuropil.Core;

// Shiu et al. default_params, in mV, ms and Hz.
public sealed record LifParams
{
    public double Dt { get; init; } = 0.1;
    public double VRest { get; init; } = -52;
    public double VReset { get; init; } = -52;
    public double VThreshold { get; init; } = -45;
    public double TauMembrane { get; init; } = 20;
    public double TauSynapse { get; init; } = 5;
    public double Refractory { get; init; } = 2.2;
    public double Delay { get; init; } = 1.8;
    public double WeightPerSynapse { get; init; } = 0.275;
    public double PoissonRate { get; init; } = 150;
    public double PoissonScale { get; init; } = 250;

    public static LifParams Default { get; } = new();

    public int DelaySteps => (int)Math.Round(Delay / Dt);
    public int RefractorySteps => (int)Math.Round(Refractory / Dt);
    public double PoissonKick => WeightPerSynapse * PoissonScale;
    public ulong PoissonThreshold(double? rateHz = null) => Rng.Threshold((rateHz ?? PoissonRate) * Dt / 1000);

    // Exact one-step solution of dv/dt = (VRest - v + g) / TauMembrane, dg/dt = -g / TauSynapse.
    public double DecayV => Math.Exp(-Dt / TauMembrane);
    public double DecayG => Math.Exp(-Dt / TauSynapse);
    public double GToV => TauSynapse / (TauSynapse - TauMembrane) * (DecayG - DecayV);
}
