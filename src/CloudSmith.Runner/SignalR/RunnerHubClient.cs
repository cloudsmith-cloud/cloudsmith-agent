// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0
// TODO(AB#1664-followup): rename type to Agent

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace CloudSmith.Runner.SignalR;

public sealed class RunnerHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<RunnerHubClient> _logger;

    public RunnerHubClient(IOptions<RunnerOptions> opts, ILogger<RunnerHubClient> logger)
    {
        _logger = logger;
        _connection = new HubConnectionBuilder()
            .WithUrl($"{opts.Value.ApiBaseUrl}/hubs/runner", options =>
            {
                // mTLS: Agent presents its enrolled certificate on the connection
                options.ClientCertificates.Add(GetRunnerCertificate(opts.Value.CertThumbprint));
            })
            .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) })
            .Build();

        _connection.On<string, string>("DispatchJob", OnJobDispatched);
        _connection.Reconnected += id => { _logger.LogInformation("Agent reconnected. Connection: {Id}", id); return Task.CompletedTask; };
        _connection.Closed += ex => { _logger.LogWarning(ex, "Agent SignalR connection closed"); return Task.CompletedTask; };
    }

    public HubConnectionState State => _connection.State;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("Connecting Agent to control channel...");
        await _connection.StartAsync(ct);
        _logger.LogInformation("Agent connected. ConnectionId: {Id}", _connection.ConnectionId);
    }

    public async Task SendHeartbeatAsync(CancellationToken ct)
    {
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("Heartbeat", Environment.MachineName, ct);
    }

    private Task OnJobDispatched(string jobId, string payloadJson)
    {
        _logger.LogInformation("Job dispatched: {JobId}", jobId);
        // Job execution is delegated to the Agent worker via a channel
        return Task.CompletedTask;
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 GetRunnerCertificate(string thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint))
            throw new InvalidOperationException("CS-RUNNER-ERR-001: Agent cert thumbprint is not configured. Run Agent enrollment first.");

        var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);

        var cert = store.Certificates
            .Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, thumbprint, false)
            .OfType<System.Security.Cryptography.X509Certificates.X509Certificate2>()
            .FirstOrDefault();

        store.Close();
        return cert ?? throw new InvalidOperationException($"CS-RUNNER-ERR-002: Certificate with thumbprint '{thumbprint}' not found in LocalMachine store.");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
