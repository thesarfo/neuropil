using System.Diagnostics;
using System.Threading.Channels;
using Akka.Actor;
using Neuropil.Actors;
using Neuropil.Core;

namespace Neuropil.Core.Tests;

public sealed class SessionActorTests : IAsyncLifetime
{
    static readonly int[] Stimulated = [0, 1, 2, 3, 4, 5, 6, 7];

    readonly ActorSystem _system = ActorSystem.Create("session-tests");
    readonly Channel<Frame> _frames = Channel.CreateUnbounded<Frame>();
    readonly Brain _brain = ActorSimulatorTests.RandomBrain(150, 12, 21);
    IActorRef _session = ActorRefs.Nobody;

    public Task InitializeAsync()
    {
        var setup = new SessionActor.Setup(_brain, LifParams.Default, 3, _frames.Writer);
        _session = _system.ActorOf(Props.Create(() => new SessionActor(setup)));
        _session.Tell(new Stimulate(Stimulated, 150));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _system.Terminate();

    async Task<List<Frame>> FramesUntil(Func<Frame, bool> done, double seconds = 10)
    {
        var frames = new List<Frame>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await foreach (var f in _frames.Reader.ReadAllAsync(timeout.Token))
        {
            frames.Add(f);
            if (done(f)) break;
        }
        return frames;
    }

    async Task<List<Frame>> FramesFor(double seconds)
    {
        var frames = new List<Frame>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await foreach (var f in _frames.Reader.ReadAllAsync(timeout.Token)) frames.Add(f);
        }
        catch (OperationCanceledException) { }
        return frames;
    }

    [Fact]
    public async Task Streamed_spikes_match_array_mode()
    {
        _session.Tell(new SetSpeed(null));
        _session.Tell(new Play());
        await FramesUntil(f => f.Step >= 3000);
        _session.Tell(new Pause());
        var rest = await FramesUntil(f => !f.Running);

        // Frames read by the first wait are gone, so replay from a clean session instead of stitching.
        _session.Tell(new Reset());
        await FramesUntil(f => f.Step == 0 && !f.Running);
        _session.Tell(new StepOnce(2500));
        var frames = await FramesUntil(f => f.Step >= 2500);

        var array = new ArraySimulator(_brain, seed: 3);
        array.Stimulate(Stimulated, 150);
        var expected = new List<(long, int)>();
        for (long s = 0; s < 2500; s++)
            foreach (int i in array.Advance()) expected.Add((s, i));

        Assert.DoesNotContain(frames, f => f.Truncated);
        Assert.True(expected.Count > 500, $"only {expected.Count} spikes");
        Assert.Equal(expected, frames.SelectMany(f => f.Spikes).ToList());
        Assert.NotEmpty(rest);
    }

    [Fact]
    public async Task Frames_are_capped_at_thirty_per_second_at_full_speed()
    {
        _session.Tell(new SetSpeed(null));
        _session.Tell(new Play());
        var frames = await FramesFor(1.0);

        Assert.InRange(frames.Count, 10, UiBridgeActor.FramesPerSecond + 1);
        Assert.True(frames[^1].Step > 1000, $"only reached step {frames[^1].Step}");
        Assert.Contains(frames, f => f.Rates.Count > 0);
    }

    [Fact]
    public async Task Pause_holds_the_clock_and_step_moves_it_by_exactly_one()
    {
        _session.Tell(new SetSpeed(null));
        _session.Tell(new Play());
        await FramesUntil(f => f.Step >= 500);
        _session.Tell(new Pause());
        await FramesFor(0.3);
        long paused = (await Probe()).Step;

        Assert.Empty(await FramesFor(0.3));

        _session.Tell(new StepOnce(1));
        var stepped = await FramesUntil(f => f.Step > paused);
        Assert.Equal(paused + 1, stepped[^1].Step);
    }

    [Fact]
    public async Task Speed_paces_simulated_time_against_the_wall_clock()
    {
        _session.Tell(new SetSpeed(0.1));
        _session.Tell(new Play());
        var wall = Stopwatch.StartNew();
        var frames = await FramesFor(1.0);
        double simMs = frames[^1].TimeMs;

        // 0.1x for about a second should cover about 100 ms of simulated time.
        Assert.InRange(simMs, 60, 130);
        Assert.Equal(0.1, frames[^1].Speed);
        Assert.True(wall.Elapsed.TotalSeconds < 3);
    }

    // Forces a frame so the test can read the current step while paused.
    async Task<Frame> Probe()
    {
        _session.Tell(new Pause());
        return (await FramesUntil(f => !f.Running))[^1];
    }
}
