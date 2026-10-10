using System.Text;
using Neuropil.Actors;
using Neuropil.Core;
using Neuropil.Server;

namespace Neuropil.Core.Tests;

public class ClientMessageTests
{
    static readonly Circuit Loaded = new("test", Brain.FromEdges(10, [(0, 1, 5)]), [0, 1, 2]);

    static (object? Command, string? Error) Parse(string json, Circuit? circuit = null) =>
        ClientMessage.Parse(Encoding.UTF8.GetBytes(json), circuit);

    [Fact]
    public void Load_works_before_anything_else()
    {
        var (command, error) = Parse("""{"type":"load","circuit":"sugarR"}""");
        Assert.Null(error);
        Assert.Equal(new LoadCircuit("sugarR"), command);
    }

    [Theory]
    [InlineData("""{"type":"play"}""")]
    [InlineData("""{"type":"stimulate","rateHz":150}""")]
    public void Other_commands_need_a_circuit(string json)
    {
        var (command, error) = Parse(json);
        Assert.Null(command);
        Assert.Equal("load a circuit first", error);
    }

    [Fact]
    public void Stimulate_defaults_to_the_sensory_group()
    {
        var command = Assert.IsType<Stimulate>(Parse("""{"type":"stimulate","rateHz":150}""", Loaded).Command);
        Assert.Equal([0, 1, 2], command.Neurons);
        Assert.Equal(150, command.RateHz);
    }

    [Fact]
    public void Stimulate_accepts_chosen_neurons_and_zero_to_switch_off()
    {
        var command = Assert.IsType<Stimulate>(Parse("""{"type":"stimulate","rateHz":0,"neurons":[4,9]}""", Loaded).Command);
        Assert.Equal([4, 9], command.Neurons);
        Assert.Equal(0, command.RateHz);
    }

    [Fact]
    public void Speed_takes_a_ratio_or_max()
    {
        Assert.Equal(new SetSpeed(0.05), Parse("""{"type":"speed","value":0.05}""", Loaded).Command);
        Assert.Equal(new SetSpeed(null), Parse("""{"type":"speed","value":"max"}""", Loaded).Command);
    }

    [Fact]
    public void Step_defaults_to_one()
    {
        Assert.Equal(new StepOnce(1), Parse("""{"type":"step"}""", Loaded).Command);
        Assert.Equal(new StepOnce(18), Parse("""{"type":"step","steps":18}""", Loaded).Command);
    }

    [Fact]
    public void Watch_takes_a_neuron_or_null()
    {
        Assert.Equal(new Watch(7), Parse("""{"type":"watch","neuron":7}""", Loaded).Command);
        Assert.Equal(new Watch(null), Parse("""{"type":"watch","neuron":null}""", Loaded).Command);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"watch"}""")]
    [InlineData("""{"type":"watch","neuron":10}""")]
    [InlineData("[1,2]")]
    [InlineData("""{"circuit":"x"}""")]
    [InlineData("""{"type":"launch"}""")]
    [InlineData("""{"type":"load"}""")]
    [InlineData("""{"type":"stimulate"}""")]
    [InlineData("""{"type":"stimulate","rateHz":-1}""")]
    [InlineData("""{"type":"stimulate","rateHz":5000}""")]
    [InlineData("""{"type":"stimulate","rateHz":"fast"}""")]
    [InlineData("""{"type":"stimulate","rateHz":100,"neurons":[10]}""")]
    [InlineData("""{"type":"stimulate","rateHz":100,"neurons":[-1]}""")]
    [InlineData("""{"type":"stimulate","rateHz":100,"neurons":"all"}""")]
    [InlineData("""{"type":"step","steps":0}""")]
    [InlineData("""{"type":"step","steps":1.5}""")]
    [InlineData("""{"type":"step","steps":10000000}""")]
    [InlineData("""{"type":"speed"}""")]
    [InlineData("""{"type":"speed","value":0}""")]
    [InlineData("""{"type":"speed","value":"fast"}""")]
    [InlineData("""{"type":"speed","value":1e9}""")]
    public void Bad_messages_are_rejected_with_an_error(string json)
    {
        var (command, error) = Parse(json, Loaded);
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
