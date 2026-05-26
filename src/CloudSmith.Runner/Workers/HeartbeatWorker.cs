// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner.Pushers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Workers;

/// <summary>
/// Sends a heartbeat to the Relay LAN listener every 60 seconds (AB#1458).
/// On consecutive failures, applies exponential backoff up to 5 minutes.
/// Heartbeat keeps the Relay's last-seen timestamp fresh so PaaS can detect
/// disconnected Agents.
/// </summary>
public sealed class HeartbeatWorker : BackgroundService
{
    private static readonly TimeSpan BaseInterval    = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff      = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialStagger  = TimeSpan.FromSeconds(20);

    private readonly RelayPusher _pusher;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(RelayPusher pusher, ILogger<HeartbeatWorker> logger)
    {
        _pusher = pusher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("HeartbeatWorker starting — interval {Interval}s", BaseInterval.TotalSeconds);

        // Stagger so enrollment finishes before first heartbeat.
        try { await Task.Delay(InitialStagger, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pusher.HeartbeatAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;

                // Normal interval after success.
                await Task.Delay(BaseInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // graceful shutdown
            }
            catch (Exception ex)
            {
                consecutiveFailures++;

                // Exponential backoff: 60s * 2^(failures-1), capped at MaxBackoff.
                var backoff = TimeSpan.FromSeconds(
                    Math.Min(MaxBackoff.TotalSeconds,
                             BaseInterval.TotalSeconds * Math.Pow(2, consecutiveFailures - 1)));

                _logger.LogWarning(ex,
                    "Heartbeat failed (attempt {N}) — retrying in {Backoff}s",
                    consecutiveFailures, (int)backoff.TotalSeconds);

                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
