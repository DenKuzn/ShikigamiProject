using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Server;
using Shikigami.Core.Services;
using Shikigami.Core.State;
using Shikigami.Server.Http;
using Shikigami.Server.Mcp;
using Shikigami.Server.Ui;

// ── Shared state ──
var state = new ShikigamiState();
var idGen = new IdGenerator(state);
var poolService = new PoolService(state);
var launcher = new LaunchService(state, idGen, poolService);

// ── Pick a free port for HTTP ──
using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
{
    sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    state.HttpPort = ((IPEndPoint)sock.LocalEndPoint!).Port;
}

// ── Build HTTP server (Kestrel on the picked port) ──
var httpBuilder = WebApplication.CreateSlimBuilder();
httpBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, state.HttpPort));
httpBuilder.Logging.ClearProviders();

var httpApp = httpBuilder.Build();
httpApp.MapAgentEndpoints(state, launcher);
httpApp.MapPoolEndpoints(state, poolService, launcher);

await httpApp.StartAsync();
Console.Error.WriteLine($"[shikigami-mcp] HTTP server listening on 127.0.0.1:{state.HttpPort}");

// ── Start status dashboard (fire-and-forget, daemon thread) ──
StatusWindowLauncher.Start(state);

// ── Start PID monitor ──
var cts = new CancellationTokenSource();
var pidMonitor = new PidMonitor(state);
_ = Task.Run(() => pidMonitor.RunAsync(cts.Token));

// ── Build and run MCP stdio server (blocks until stdin closes) ──
var mcpHostBuilder = Host.CreateDefaultBuilder(args);
mcpHostBuilder.ConfigureLogging(l => l.ClearProviders());
mcpHostBuilder.ConfigureServices(services =>
{
    // Register shared instances for DI
    services.AddSingleton(state);
    services.AddSingleton(launcher);
    services.AddSingleton(poolService);

    services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "ShikigamiMCP", Version = "1.0.0" };
    })
    .WithStdioServerTransport()
    .WithTools<ShikigamiMcpTools>();
});

var mcpHost = mcpHostBuilder.Build();

try
{
    await mcpHost.RunAsync();
}
finally
{
    cts.Cancel();
    await httpApp.StopAsync();
    Console.Error.WriteLine("[shikigami-mcp] Server stopped");
}
