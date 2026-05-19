// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner.SignalR;
using Microsoft.Extensions.Options;

namespace CloudSmith.Runner;

public sealed class HeartbeatWorker : BackgroundService
{
    private readonly RunnerHubClient _hub;
    private readonly RunnerOptions _opts;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(RunnerHubClient hub, IOptions<RunnerOptions> opts, ILogger<HeartbeatWorker> logger)
    {
        _hub = hub;
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_opts.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _hub.SendHeartbeatAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed — will retry in {Interval}s", _opts.HeartbeatIntervalSeconds);
            }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
