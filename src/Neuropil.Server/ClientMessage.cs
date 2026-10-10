using System.Text.Json;
using Neuropil.Actors;

namespace Neuropil.Server;

public sealed record LoadCircuit(string Name);

/// <summary>
/// Parses and validates one message from the browser. Returns either a command or an error
/// to send back; nothing from the client reaches an actor unchecked.
///
///   {"type":"load","circuit":"sugarR"}
///   {"type":"stimulate","rateHz":150,"neurons":[0,1]}   neurons defaults to the circuit's sensory group
///   {"type":"play"} {"type":"pause"} {"type":"reset"}
///   {"type":"step","steps":1}
///   {"type":"speed","value":0.05}                        or "max"
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
