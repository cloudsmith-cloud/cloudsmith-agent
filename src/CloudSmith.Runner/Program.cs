// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner;
using CloudSmith.Runner.Enrollment;
using CloudSmith.Runner.Inventory;
using CloudSmith.Runner.Pushers;
using CloudSmith.Runner.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// ---------------------------------------------------------------------------
// Configuration — environment-driven.
// ---------------------------------------------------------------------------
var relayUrl = Environment.GetEnvironmentVariable("AGENT_RELAY_URL")
    ?? throw new InvalidOperationException("AGENT_RELAY_URL is required.");
var enrollmentToken = Environment.GetEnvironmentVariable("AGENT_ENROLLMENT_TOKEN") ?? string.Empty;
var scanIntervalStr = Environment.GetEnvironmentVariable("AGENT_SCAN_INTERVAL_SECONDS");
var scanInterval    = int.TryParse(scanIntervalStr, out var s) ? s : 300;
var clusterId       = Environment.GetEnvironmentVariable("AGENT_CLUSTER_ID") ?? "default";
var identityPath    = Environment.GetEnvironmentVariable("AGENT_IDENTITY_PATH")
    ?? EnrollmentClient.DefaultIdentityPath;

var agentOptions = new AgentOptions
{
    RelayUrl            = relayUrl,
    EnrollmentToken     = enrollmentToken,
    ScanIntervalSeconds = scanInterval,
    ClusterId           = clusterId,
    IdentityPath        = identityPath,
};

// ---------------------------------------------------------------------------
// Logging — Serilog -> stdout (Windows Service captures stdout via EventLog
// when sc.exe is configured with the appropriate log provider).
// ---------------------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "cloudsmith-agent")
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Use Windows Service lifecycle when running as a service.
    builder.Services.AddWindowsService(opts =>
    {
        opts.ServiceName = "CloudSmithAgent";
    });

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger, dispose: true);

    // Options.
    builder.Services.AddSingleton(Options.Create(agentOptions));

    // HTTP client shared by EnrollmentClient and RelayPusher.
    builder.Services.AddSingleton(_ => new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    });

    // Enrollment client — reads/writes identity from disk.
    builder.Services.AddSingleton(sp =>
        new EnrollmentClient(
            sp.GetRequiredService<HttpClient>(),
            agentOptions.RelayUrl,
            agentOptions.EnrollmentToken,
            sp.GetRequiredService<ILogger<EnrollmentClient>>(),
            agentOptions.IdentityPath));

    // Relay pusher — identity is set by EnrollmentHostedService after enrollment.
    builder.Services.AddSingleton(sp =>
        new RelayPusher(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ILogger<RelayPusher>>()));

    // Local Hyper-V scanner — no WinRM, runs in-process.
    builder.Services.AddSingleton<HyperVScanner>();

    // Enrollment hosted service runs first and sets identity on RelayPusher.
    builder.Services.AddHostedService<EnrollmentHostedService>();

    // Heartbeat and inventory workers.
    builder.Services.AddHostedService<HeartbeatWorker>();
    builder.Services.AddHostedService<InventoryWorker>();

    var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "cloudsmith-agent terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
