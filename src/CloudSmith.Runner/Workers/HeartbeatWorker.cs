// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner.Pushers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Workers;

/// <summary>
/// Sends a heartbeat to the Relay LAN listener every 30 seconds.
/// Heartbeat keeps the Relay's last-seen timestamp fresh so PaaS can detect
/// disconnected Agents.
/// </summary>
public sealed class HeartbeatWorker : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly RelayPusher _pusher;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(RelayPusher pusher, ILogger<HeartbeatWorker> logger)
    {
        _pusher = pusher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("HeartbeatWorker starting — interval {Interval}s", HeartbeatInterval.TotalSeconds);

        // Stagger so enrollment finishes before first heartbeat.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _pusher.HeartbeatAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Heartbeat failed — will retry next interval");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }
}
