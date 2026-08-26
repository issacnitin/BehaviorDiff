using RealDiff.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Runs must survive a restart, so the store is rooted at an explicit directory rather than the
// process working directory, which an MCP client controls and may change between launches.
RunStore.Root = Environment.GetEnvironmentVariable("REALDIFF_RUNS")
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".realdiff", "runs");
Directory.CreateDirectory(RunStore.Root);

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdio is the transport, so stdout is the protocol channel. Anything logged there corrupts it.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(RealDiffTools).Assembly);

await builder.Build().RunAsync().ConfigureAwait(false);
