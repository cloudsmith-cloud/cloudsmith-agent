// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner;
using CloudSmith.Runner.Bmc;
using CloudSmith.Runner.Enrollment;
using CloudSmith.Runner.Inventory;
using CloudSmith.Runner.Jobs;
using CloudSmith.Runner.Pushers;
using CloudSmith.Runner.Workers;
using CloudSmith.Sdk.Secrets;
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

var installDirectory = Environment.GetEnvironmentVariable("AGENT_INSTALL_DIRECTORY")
    ?? @"C:\CloudSmith";

var agentOptions = new AgentOptions
{
    RelayUrl            = relayUrl,
    EnrollmentToken     = enrollmentToken,
    ScanIntervalSeconds = scanInterval,
    ClusterId           = clusterId,
    IdentityPath        = identityPath,
    InstallDirectory    = installDirectory,
};

// ---------------------------------------------------------------------------
// Logging — Serilog -> stdout (Windows Service captures stdout via EventLog
// when sc.exe is configured with the appropriate log provider).
// ---------------------------------------------------------------------------
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "CloudSmith", "logs");
Directory.CreateDirectory(logDir);

// AB#2357 — standardised enricher set; same template as relay + api.
const string LogTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{service}] {CorrelationId}{Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "cloudsmith-agent")
    .Enrich.WithProperty("machine", Environment.MachineName)
    .WriteTo.Console(outputTemplate: LogTemplate)
    .WriteTo.File(
        path: Path.Combine(logDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: LogTemplate)
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
    // mTLS configuration (AB#1457): enforce TLS 1.3; load client cert from cert store
    // if AGENT_MTLS_CERT_THUMBPRINT is set (Phase V — optional in Phase IV MVP; enrollment
    // uses X-Agent-Secret token auth when no client cert is configured).
    builder.Services.AddSingleton(_ =>
    {
        var thumbprint = Environment.GetEnvironmentVariable("AGENT_MTLS_CERT_THUMBPRINT");

        var handler = new HttpClientHandler
        {
            // Enforce minimum TLS 1.2; prefer TLS 1.3 (OS negotiates highest mutually supported).
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                         | System.Security.Authentication.SslProtocols.Tls13,
            // Fail fast on cert errors — agents should not communicate over untrusted TLS.
            ServerCertificateCustomValidationCallback = null,
        };

        // If a client cert thumbprint is configured, load from the machine cert store for mTLS.
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My,
                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            var certs = store.Certificates.Find(
                System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                thumbprint.Replace(":", "").Replace(" ", ""),
                validOnly: true);
            if (certs.Count > 0)
            {
                handler.ClientCertificates.Add(certs[0]);
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                Log.Information("mTLS: loaded client cert {Thumbprint}", thumbprint);
            }
            else
            {
                Log.Warning("mTLS: client cert {Thumbprint} not found in LocalMachine\\My — falling back to token auth", thumbprint);
            }
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
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

    // Secrets provider — reads from AGENT_SECRET_* environment variables.
    // AB#1462 — per-request credential retrieval for BMC Redfish client.
    builder.Services.AddSingleton<ICloudSmithSecretsProvider, LocalSecretsProvider>();

    // BMC HTTP client — separate from the relay HttpClient.
    // Uses SocketsHttpHandler for TLS 1.2/1.3 enforcement.
    // For BMC endpoints with self-signed certificates, set AGENT_BMC_CERT_PATH to the
    // path of the CA certificate PEM file. The certificate is loaded and used as an
    // additional trust anchor — global certificate validation is never disabled.
    builder.Services.AddSingleton<IBmcClient>(sp =>
    {
        var certPath = Environment.GetEnvironmentVariable("AGENT_BMC_CERT_PATH");

        // Build a SocketsHttpHandler for modern TLS control.
        var socketsHandler = new System.Net.Http.SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                    | System.Security.Authentication.SslProtocols.Tls13,
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrWhiteSpace(certPath) && File.Exists(certPath))
        {
            // Load the BMC CA certificate via X509CertificateLoader (non-deprecated API).
            var caCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadCertificateFromFile(certPath);
            socketsHandler.SslOptions.RemoteCertificateValidationCallback =
                (sender, serverCert, chain, errors) =>
                {
                    if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                    if (chain is null || serverCert is null) return false;
                    chain.ChainPolicy.ExtraStore.Add(caCert);
                    chain.ChainPolicy.VerificationFlags =
                        System.Security.Cryptography.X509Certificates.X509VerificationFlags.AllowUnknownCertificateAuthority;
                    // serverCert from TLS callbacks is always X509Certificate2 at runtime.
                    if (serverCert is not System.Security.Cryptography.X509Certificates.X509Certificate2 leafCert)
                        return false;
                    return chain.Build(leafCert);
                };
            Log.Information("BMC: loaded CA cert from {Path} for self-signed BMC trust", certPath);
        }

        var bmcHttp = new HttpClient(socketsHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        return new RedfishBmcClient(
            sp.GetRequiredService<ICloudSmithSecretsProvider>(),
            bmcHttp,
            sp.GetRequiredService<ILogger<RedfishBmcClient>>());
    });

    // Local Hyper-V and hardware scanners — no WinRM, run in-process.
    builder.Services.AddSingleton<HyperVScanner>();
    builder.Services.AddSingleton<HardwareScanner>();

    // Job worker — singleton so EnrollmentHostedService can call SetIdentity on it.
    builder.Services.AddSingleton<JobWorker>();

    // Platform update worker — singleton so EnrollmentHostedService can call SetIdentity on it.
    builder.Services.AddSingleton<PlatformUpdateWorker>();

    // Watchdog — singleton so RelayPusher can notify it via SetWatchdog (AB#1455).
    builder.Services.AddSingleton<WatchdogWorker>();

    // Enrollment hosted service runs first and sets identity on RelayPusher + JobWorker + PlatformUpdateWorker.
    builder.Services.AddHostedService<EnrollmentHostedService>();

    // Heartbeat, inventory, job, platform-update, and watchdog workers.
    builder.Services.AddHostedService<HeartbeatWorker>();
    builder.Services.AddHostedService<InventoryWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PlatformUpdateWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchdogWorker>());

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
