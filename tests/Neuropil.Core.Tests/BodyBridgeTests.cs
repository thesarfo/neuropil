using System.Threading.Channels;
using Akka.Actor;
using Neuropil.Actors;
using Neuropil.Core;

namespace Neuropil.Core.Tests;

public sealed class BodyBridgeTests : IAsyncLifetime
{
    static readonly int[] Sensory = [0, 1, 2, 3, 4, 5, 6, 7];

    readonly ActorSystem _system = ActorSystem.Create("body-tests");
    readonly Channel<Frame> _frames = Channel.CreateUnbounded<Frame>();
    // Feed-forward on purpose: 8 sensors -> 6 interneurons -> 3 motor neurons, with no loops, so
    // activity follows the stimulus and stops when it stops.
    static readonly int[] Inter = [8, 9, 10, 11, 12, 13];
    static readonly int[] Motor = [14, 15, 16];

    readonly Brain _brain = Brain.FromEdges(17,
    [
        .. from s in Sensory from i in Inter select (s, i, (short)30),
        .. from i in Inter from m in Motor select (i, m, (short)30),
    ]);
    IActorRef _session = ActorRefs.Nobody;

    public Task InitializeAsync()
    {
        int[] motor = Motor;
        var plan = new BodyPlan(
            new Dictionary<string, (IReadOnlyList<int>, double)> { ["food"] = (Sensory, 150) },
            new Dictionary<string, IReadOnlyList<int>> { ["mouth"] = motor });
        var setup = new SessionActor.Setup(_brain, LifParams.Default, 3, _frames.Writer, Body: plan);
        _session = _system.ActorOf(Props.Create(() => new SessionActor(setup)));
        _session.Tell(new SetSpeed(null));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _system.Terminate();

    async Task<Frame> FrameWhere(Func<Frame, bool> wanted, double seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await foreach (var f in _frames.Reader.ReadAllAsync(timeout.Token))
            if (wanted(f)) return f;
        throw new TimeoutException();
    }

    static double Mouth(Frame f) => f.Motor?.FirstOrDefault(m => m.Motor == "mouth").Hz ?? -1;

    [Fact]
    public async Task Sensing_drives_the_motor_neurons_and_stopping_lets_them_fall_silent()
    {
        _session.Tell(new Play());
        var quiet = await FrameWhere(f => f.Motor is not null && f.Step > 500);
        Assert.Equal(0, Mouth(quiet));

        _session.Tell(new Sense(new Dictionary<string, double> { ["food"] = 1 }));
        var fed = await FrameWhere(f => Mouth(f) > 20);

        _session.Tell(new Sense(new Dictionary<string, double> { ["food"] = 0 }));
        var after = await FrameWhere(f => f.Step > fed.Step && Mouth(f) == 0);
        Assert.True(after.Step > fed.Step);
    }

    [Fact]
    public async Task A_weaker_sensation_drives_the_motor_neurons_less()
    {
        async Task<double> SettledRate(double level)
        {
            _session.Tell(new Reset());
            await FrameWhere(f => f.Step == 0 && !f.Running);
            _session.Tell(new Sense(new Dictionary<string, double> { ["food"] = level }));
            _session.Tell(new Play());
            // An idle brain runs far ahead before the first stimulus lands, so measure from the response.
            long start = (await FrameWhere(f => Mouth(f) > 0)).Step + 1000;
            var frames = new List<Frame>();
            await FrameWhere(f => { if (f.Step > start && f.Motor is not null) frames.Add(f); return f.Step > start + 6000; });
            _session.Tell(new Pause());
            return frames.Average(Mouth);
        }

        double strong = await SettledRate(1);
        double weak = await SettledRate(0.4);
        Assert.True(strong > weak + 5, $"strong {strong:F1} Hz, weak {weak:F1} Hz");
    }

    [Fact]
    public async Task Unknown_sensors_are_ignored()
    {
        _session.Tell(new Sense(new Dictionary<string, double> { ["nose"] = 1 }));
        _session.Tell(new Play());
        var later = await FrameWhere(f => f.Motor is not null && f.Step > 2000);
        Assert.Equal(0, Mouth(later));
    }
}
