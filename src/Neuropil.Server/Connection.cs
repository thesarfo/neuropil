using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
using Neuropil.Actors;
using Neuropil.Core;

namespace Neuropil.Server;

/// <summary>One browser tab: its WebSocket, and the session actor it drives.</summary>
public sealed class Connection(WebSocket socket, CircuitLibrary library, Atlas? atlas, ActorSystem system, ILogger logger)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly SemaphoreSlim _sendLock = new(1, 1);

    // If the browser can't keep up, it gets the newest frames and misses old ones.
    readonly Channel<Frame> _frames = Channel.CreateBounded<Frame>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    Circuit? _circuit;
    IActorRef _session = ActorRefs.Nobody;

    public async Task RunAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var pump = PumpFramesAsync(linked.Token);
        try
        {
            await SendAsync(new
            {
                type = "hello",
                circuits = library.All.Select(c => new { name = c.Name, title = c.Info?.Title ?? c.Name, neurons = c.Brain.NeuronCount, edges = c.Brain.EdgeCount }),
                brain = atlas is null ? null : new { name = Atlas.Name, neurons = atlas.Brain.NeuronCount, edges = atlas.Brain.EdgeCount, fly = atlas.Body is null ? null : Atlas.FlyName },
            }, linked.Token);
            await ReceiveAsync(linked.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            logger.LogDebug(e, "connection ended");
        }
        finally
        {
            linked.Cancel();
            if (!_session.IsNobody()) system.Stop(_session);
            await pump.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    async Task ReceiveAsync(CancellationToken stop)
    {
        var buffer = new byte[ClientMessage.MaxBytes];
        while (socket.State == WebSocketState.Open)
        {
            int length = 0;
            ValueWebSocketReceiveResult part;
            do
            {
                if (length == buffer.Length)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", stop);
                    return;
                }
                part = await socket.ReceiveAsync(buffer.AsMemory(length), stop);
                length += part.Count;
            } while (!part.EndOfMessage);

            if (part.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, stop);
                return;
            }

            var (command, error) = ClientMessage.Parse(buffer.AsSpan(0, length), _circuit);
            if (error is not null) await SendAsync(new { type = "error", message = error }, stop);
            else if (command is LoadCircuit load) await LoadAsync(load.Name, stop);
            else if (command is Neighbours of) await SendNeighboursAsync(of.Neuron, stop);
            else _session.Tell(command!);
        }
    }

    async Task SendNeighboursAsync(int neuron, CancellationToken stop)
    {
        if (atlas is null || _circuit?.Brain != atlas.Brain) return;
        var (inputs, outputs) = atlas.Neighbours(neuron);
        await SendAsync(new { type = "neighbours", neuron, id = atlas.Brain.FlywireIds[neuron].ToString(), inputs, outputs }, stop);
    }

    async Task LoadBrainAsync(Atlas atlas, bool withBody, CancellationToken stop)
    {
        var body = withBody ? atlas.Body : null;
        if (!_session.IsNobody()) system.Stop(_session);
        _circuit = new Circuit(Atlas.Name, atlas.Brain, [], Sensors: body?.Sensors.Keys.ToHashSet());
        await SendAsync(new
        {
            type = "brain",
            name = body is null ? Atlas.Name : Atlas.FlyName,
            body = body is null ? null : new { sensors = body.Sensors.Keys, motors = body.Motors.Keys, periodMs = BodyBridgeActor.PeriodMs },
            dtMs = LifParams.Default.Dt,
            neurons = atlas.Brain.NeuronCount,
            actors = atlas.Actors,
            inhibitory = atlas.Inhibitory,
            labels = atlas.Labels.Select(kv => new { neuron = kv.Key, label = kv.Value }),
            stimuli = atlas.Stimuli.Select(s => new { name = s.Name, info = s.Info, sensory = s.Sensory }),
        }, stop);

        var setup = new SessionActor.Setup(atlas.Brain, LifParams.Default, 1, _frames.Writer, atlas.UnitOf, body);
        _session = system.ActorOf(Props.Create(() => new SessionActor(setup)));
    }

    async Task LoadAsync(string name, CancellationToken stop)
    {
        if (atlas is not null && (name == Atlas.Name || (name == Atlas.FlyName && atlas.Body is not null)))
        {
            await LoadBrainAsync(atlas, name == Atlas.FlyName, stop);
            return;
        }
        if (library.Find(name) is not { } circuit)
        {
            await SendAsync(new { type = "error", message = $"no circuit named \"{name}\"" }, stop);
            return;
        }

        if (!_session.IsNobody()) system.Stop(_session);
        _circuit = circuit;
        var brain = circuit.Brain;
        var sensory = circuit.Sensory.ToHashSet();

        var edges = new List<int[]>();
        for (int i = 0; i < brain.NeuronCount; i++)
        {
            var targets = brain.TargetsOf(i);
            var weights = brain.WeightsOf(i);
            for (int k = 0; k < targets.Length; k++) edges.Add([i, targets[k], weights[k]]);
        }

        // FlyWire IDs exceed what a JavaScript number holds exactly, so they travel as strings.
        await SendAsync(new
        {
            type = "circuit",
            name = circuit.Name,
            info = circuit.Info ?? new CircuitInfo(circuit.Name, "Stimulate", "Stop stimulating", ""),
            dtMs = LifParams.Default.Dt,
            neurons = brain.FlywireIds.Select((id, i) => new { id = id.ToString(), sensory = sensory.Contains(i), label = circuit.Labels?.GetValueOrDefault(i) }),
            edges,
        }, stop);

        var setup = new SessionActor.Setup(brain, LifParams.Default, 1, _frames.Writer);
        _session = system.ActorOf(Props.Create(() => new SessionActor(setup)));
    }

    async Task PumpFramesAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var f in _frames.Reader.ReadAllAsync(stop))
            {
                await SendAsync(new
                {
                    type = "frame",
                    step = f.Step,
                    timeMs = f.TimeMs,
                    running = f.Running,
                    speed = f.Speed,
                    spikes = f.Spikes.Select(s => new[] { s.Step, s.Neuron }),
                    rates = f.Rates.Select(r => new[] { r.Neuron, r.Hz }),
                    truncated = f.Truncated,
                    motor = f.Motor?.ToDictionary(m => m.Motor, m => Math.Round(m.Hz, 1)),
                    trace = f.Trace is null ? null : new
                    {
                        neuron = f.Trace.Neuron,
                        fromStep = f.Trace.FromStep,
                        mv = f.Trace.Millivolts.Select(v => Math.Round(v, 2)),
                    },
                }, stop);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
    }

    async Task SendAsync(object message, CancellationToken stop)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await _sendLock.WaitAsync(stop);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
