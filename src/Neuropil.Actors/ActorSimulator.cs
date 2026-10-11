using Akka.Actor;
using Akka.Configuration;
using Neuropil.Core;

namespace Neuropil.Actors;

public sealed class ActorSimulator : IAsyncDisposable
{
    readonly ActorSystem _system;
    readonly IActorRef _clock;

    public ActorSystem System => _system;
    public IActorRef Clock => _clock;

    public ActorSimulator(
        Brain brain,
        IEnumerable<int> stimulated,
        LifParams? p = null,
        ulong seed = 0,
        int trial = 0,
        double? rateHz = null,
        IEnumerable<int>? silenced = null,
        int[]? regionOf = null)
    {
        _system = ActorSystem.Create("neuropil", ConfigurationFactory.ParseString("""
            akka.loglevel = WARNING
            akka.stdout-loglevel = WARNING
            """));
        var setup = new ClockActor.Setup(brain, p ?? LifParams.Default, seed, trial,
            [.. stimulated], rateHz, [.. silenced ?? []], regionOf);
        _clock = _system.ActorOf(Props.Create(() => new ClockActor(setup)), "clock");
    }

    public async Task<IReadOnlyList<(long Step, int Neuron)>> RunAsync(long steps)
    {
        var result = await _clock.Ask<RunResult>(new Run(steps), TimeSpan.FromHours(1));
        return result.Spikes;
    }

    public void Stimulate(IEnumerable<int> neurons, double rateHz) => _clock.Tell(new Stimulate([.. neurons], rateHz));

    public async ValueTask DisposeAsync() => await _system.Terminate();
}
