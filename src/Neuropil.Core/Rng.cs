namespace Neuropil.Core;

// Counter-based, so draws don't depend on execution order. Keyed by FlyWire ID so a circuit
// sees the same stream as the whole brain.
public static class Rng
{
    public static ulong Draw(ulong seed, int trial, long flywireId, long step)
    {
        ulong x = Mix(seed + 0x9E3779B97F4A7C15UL);
        x = Mix(x ^ (ulong)trial);
        x = Mix(x ^ (ulong)flywireId);
        return Mix(x ^ (ulong)step);
    }

    public static ulong Threshold(double probability) => (ulong)(Math.Clamp(probability, 0, 1) * ulong.MaxValue);

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
