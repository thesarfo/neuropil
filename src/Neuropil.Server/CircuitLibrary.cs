using Neuropil.Core;

namespace Neuropil.Server;

public sealed record Circuit(string Name, Brain Brain, IReadOnlyList<int> Sensory);

/// <summary>Circuits cut in advance with `neuropil circuit`; the preset of the same name marks the sensory neurons.</summary>
public sealed class CircuitLibrary
{
    readonly Dictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);

    public CircuitLibrary(IEnumerable<Circuit> circuits)
    {
        foreach (var c in circuits) _circuits[c.Name] = c;
    }

    public IEnumerable<Circuit> All => _circuits.Values;

    public Circuit? Find(string name) => _circuits.GetValueOrDefault(name);

    public static CircuitLibrary Load(string circuitsDir, string presetsDir)
    {
        var circuits = new List<Circuit>();
        if (!Directory.Exists(circuitsDir)) return new CircuitLibrary(circuits);

        foreach (string path in Directory.EnumerateFiles(circuitsDir, "*.bin").Order())
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var brain = Brain.Load(path);
            string preset = Path.Combine(presetsDir, name + ".txt");
            var sensory = File.Exists(preset)
                ? File.ReadLines(preset)
                    .Where(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'))
                    .Select(l => brain.IndexOf(long.Parse(l)))
                    .Where(i => i >= 0)
                    .ToList()
                : [];
            circuits.Add(new Circuit(name, brain, sensory));
        }
        return new CircuitLibrary(circuits);
    }
}
