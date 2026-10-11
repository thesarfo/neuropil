using System.Text.Json;
using Neuropil.Actors;

namespace Neuropil.Server;

public sealed record LoadCircuit(string Name);

public sealed record Neighbours(int Neuron);

/// <summary>
/// Parses and validates one message from the browser. Returns either a command or an error
/// to send back; nothing from the client reaches an actor unchecked.
///
///   {"type":"load","circuit":"sugarR"}
///   {"type":"stimulate","rateHz":150,"neurons":[0,1]}   neurons defaults to the circuit's sensory group
///   {"type":"play"} {"type":"pause"} {"type":"reset"}
///   {"type":"step","steps":1}
///   {"type":"speed","value":0.05}                        or "max"
///   {"type":"watch","neuron":33}                         or null to stop; streams that neuron's voltage
///   {"type":"silence","neurons":[33],"silenced":true}    cut those neurons' outputs, or restore them
///   {"type":"neighbours","neuron":33}                    whole brain only: strongest inputs and outputs
///   {"type":"sense","levels":{"sugar":0.6}}              embodied fly only: sensor levels from 0 to 1
/// </summary>
public static class ClientMessage
{
    public const int MaxBytes = 16 * 1024;
    public const double MaxRateHz = 1000;
    public const long MaxSteps = 100_000;
    public const double MinSpeed = 0.0001, MaxSpeed = 100;

    public static (object? Command, string? Error) Parse(ReadOnlySpan<byte> json, Circuit? circuit)
    {
        JsonDocument doc;
        try
        {
            var reader = new Utf8JsonReader(json);
            doc = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return Fail("message is not valid JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                return Fail("message needs a string \"type\"");
            string type = typeProp.GetString()!;

            if (type == "load")
            {
                return root.TryGetProperty("circuit", out var c) && c.ValueKind == JsonValueKind.String
                    ? (new LoadCircuit(c.GetString()!), null)
                    : Fail("load needs a \"circuit\" name");
            }

            if (circuit is null) return Fail("load a circuit first");

            switch (type)
            {
                case "play": return (new Play(), null);
                case "pause": return (new Pause(), null);
                case "reset": return (new Reset(), null);

                case "step":
                {
                    long steps = 1;
                    if (root.TryGetProperty("steps", out var s) && (!s.TryGetInt64(out steps) || steps < 1 || steps > MaxSteps))
                        return Fail($"steps must be a whole number from 1 to {MaxSteps}");
                    return (new StepOnce(steps), null);
                }

                case "speed":
                {
                    if (!root.TryGetProperty("value", out var v)) return Fail("speed needs a \"value\"");
                    if (v.ValueKind == JsonValueKind.String && v.GetString() == "max") return (new SetSpeed(null), null);
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double ratio) || !(ratio >= MinSpeed && ratio <= MaxSpeed))
                        return Fail($"speed must be \"max\" or a number from {MinSpeed} to {MaxSpeed}");
                    return (new SetSpeed(ratio), null);
                }

                case "watch":
                {
                    if (!root.TryGetProperty("neuron", out var w)) return Fail("watch needs a \"neuron\" index or null");
                    if (w.ValueKind == JsonValueKind.Null) return (new Watch(null), null);
                    if (!w.TryGetInt32(out int watched) || watched < 0 || watched >= circuit.Brain.NeuronCount)
                        return Fail($"neuron must be null or an index from 0 to {circuit.Brain.NeuronCount - 1}");
                    return (new Watch(watched), null);
                }

                case "silence":
                {
                    if (!root.TryGetProperty("silenced", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Fail("silence needs \"silenced\": true or false");
                    if (!root.TryGetProperty("neurons", out var n) || n.ValueKind != JsonValueKind.Array || n.GetArrayLength() == 0)
                        return Fail("silence needs a non-empty \"neurons\" array");
                    var list = new List<int>();
                    foreach (var item in n.EnumerateArray())
                    {
                        if (!item.TryGetInt32(out int i) || i < 0 || i >= circuit.Brain.NeuronCount)
                            return Fail($"neuron indexes must be from 0 to {circuit.Brain.NeuronCount - 1}");
                        list.Add(i);
                    }
                    return (new Silence(list, flag.GetBoolean()), null);
                }

                case "sense":
                {
                    if (circuit.Sensors is not { } sensors) return Fail("this circuit has no body");
                    if (!root.TryGetProperty("levels", out var l) || l.ValueKind != JsonValueKind.Object) return Fail("sense needs a \"levels\" object");
                    var levels = new Dictionary<string, double>();
                    foreach (var entry in l.EnumerateObject())
                    {
                        if (!sensors.Contains(entry.Name)) return Fail($"unknown sensor \"{entry.Name}\"");
                        if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetDouble(out double level) || !(level >= 0 && level <= 1))
                            return Fail("sensor levels must be numbers from 0 to 1");
                        levels[entry.Name] = level;
                    }
                    return (new Sense(levels), null);
                }

                case "neighbours":
                {
                    if (!root.TryGetProperty("neuron", out var n) || !n.TryGetInt32(out int of) || of < 0 || of >= circuit.Brain.NeuronCount)
                        return Fail($"neuron must be an index from 0 to {circuit.Brain.NeuronCount - 1}");
                    return (new Neighbours(of), null);
                }

                case "stimulate":
                {
                    if (!root.TryGetProperty("rateHz", out var r) || r.ValueKind != JsonValueKind.Number || !r.TryGetDouble(out double rate) || !(rate >= 0 && rate <= MaxRateHz))
                        return Fail($"rateHz must be a number from 0 to {MaxRateHz}");

                    IReadOnlyList<int> neurons = circuit.Sensory;
                    if (root.TryGetProperty("neurons", out var n))
                    {
                        if (n.ValueKind != JsonValueKind.Array) return Fail("neurons must be an array of neuron indexes");
                        var list = new List<int>();
                        foreach (var item in n.EnumerateArray())
                        {
                            if (!item.TryGetInt32(out int i) || i < 0 || i >= circuit.Brain.NeuronCount)
                                return Fail($"neuron indexes must be from 0 to {circuit.Brain.NeuronCount - 1}");
                            list.Add(i);
                        }
                        neurons = list;
                    }
                    return (new Stimulate(neurons, rate), null);
                }

                default:
                    return Fail($"unknown message type \"{type}\"");
            }
        }
    }

    static (object?, string?) Fail(string error) => (null, error);
}
