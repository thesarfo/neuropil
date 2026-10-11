using Akka.Actor;
using Akka.Configuration;
using Neuropil.Server;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Neuropil");
string root = RepoRoot();
string circuitsDir = Path.Combine(root, settings["CircuitsDir"] ?? "data/circuits");
string presetsDir = Path.Combine(root, settings["PresetsDir"] ?? "presets");
string[] origins = settings.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5080", "http://127.0.0.1:5080", "http://localhost:5173"];

// Local tool with no login: listen on this machine only unless told otherwise.
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");

builder.Services.AddSingleton(_ => CircuitLibrary.Load(circuitsDir, presetsDir));
var atlas = Atlas.Load(
    Path.Combine(root, settings["Brain"] ?? "data/brain.bin"),
    Path.Combine(root, settings["BrainMeta"] ?? "data/brain.meta.json"),
    presetsDir);
builder.Services.AddSingleton(_ => ActorSystem.Create("neuropil", ConfigurationFactory.ParseString("""
    akka.loglevel = WARNING
    akka.stdout-loglevel = WARNING
    """)));

var app = builder.Build();

var sockets = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) };
foreach (string origin in origins) sockets.AllowedOrigins.Add(origin);
app.UseWebSockets(sockets);
app.UseDefaultFiles();
app.UseStaticFiles();

app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<ActorSystem>().Terminate().Wait(TimeSpan.FromSeconds(5)));

app.MapGet("/api/circuits", (CircuitLibrary library) =>
    library.All.Select(c => new { name = c.Name, neurons = c.Brain.NeuronCount, edges = c.Brain.EdgeCount, sensory = c.Sensory.Count }));

app.MapGet("/api/brain/meta", () => atlas is null ? Results.NotFound() : Results.File(atlas.MetaPath, "application/json"));

app.Map("/ws", async (HttpContext context, CircuitLibrary library, ActorSystem system, ILogger<Connection> logger) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await new Connection(socket, library, atlas, system, logger).RunAsync(context.RequestAborted);
});

var circuits = app.Services.GetRequiredService<CircuitLibrary>().All.ToList();
app.Logger.LogInformation("{Count} circuit(s) in {Dir}: {Names}", circuits.Count, Path.GetFullPath(circuitsDir), string.Join(", ", circuits.Select(c => c.Name)));

if (atlas is not null) app.Logger.LogInformation("Whole brain: {Neurons} neurons in {Actors} region actors", atlas.Brain.NeuronCount, atlas.Actors);

app.Run();

// `dotnet run` starts in the project folder, so look upward for the solution.
static string RepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Neuropil.slnx"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}
