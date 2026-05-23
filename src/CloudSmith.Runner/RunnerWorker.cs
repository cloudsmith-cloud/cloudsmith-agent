// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0
// TODO(AB#1664-followup): rename type to Agent

using CloudSmith.Runner.SignalR;

namespace CloudSmith.Runner;

public sealed class RunnerWorker : BackgroundService
{
    private readonly RunnerHubClient _hub;
    private readonly ILogger<RunnerWorker> _logger;

    public RunnerWorker(RunnerHubClient hub, ILogger<RunnerWorker> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent starting — connecting to control channel");

        try
        {
            await _hub.ConnectAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect Agent to control channel. Will retry on reconnect.");
        }

        // Keep alive until shutdown — reconnection is handled by RunnerHubClient's WithAutomaticReconnect
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent shutting down");
        await base.StopAsync(cancellationToken);
        await _hub.DisposeAsync();
    }
}
