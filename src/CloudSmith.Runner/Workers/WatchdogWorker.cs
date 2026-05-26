// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Workers;

/// <summary>
/// Watchdog timer that monitors HeartbeatWorker liveness.
///
/// If no heartbeat has been sent successfully within 120 seconds the watchdog
/// logs an audit event and triggers an in-process restart by requesting
/// application shutdown. The Windows Service manager restarts the process per
/// its configured recovery policy.
///
/// The watchdog is reset every time RelayPusher successfully sends a heartbeat
/// via <see cref="RecordHeartbeat"/>.
///
/// AB#1455
/// </summary>
public sealed class WatchdogWorker : BackgroundService
{
    /// <summary>Maximum time allowed without a successful heartbeat before restart.</summary>
    private static readonly TimeSpan UnresponsiveThreshold = TimeSpan.FromSeconds(120);

    /// <summary>How often to check whether the threshold has been exceeded.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WatchdogWorker> _logger;

    // UTC epoch ms of the last successful heartbeat.
    // Initialised to startup time so the watchdog does not fire immediately.
    private long _lastHeartbeatMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public WatchdogWorker(
        IHostApplicationLifetime lifetime,
        ILogger<WatchdogWorker> logger)
    {
        _lifetime = lifetime;
        _logger   = logger;
    }

    /// <summary>
    /// Called by <see cref="CloudSmith.Runner.Pushers.RelayPusher"/> after each
    /// successful heartbeat HTTP call. Thread-safe.
    /// </summary>
    public void RecordHeartbeat() =>
        Interlocked.Exchange(ref _lastHeartbeatMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "WatchdogWorker starting — unresponsive threshold {Threshold}s, check interval {Check}s",
            UnresponsiveThreshold.TotalSeconds, CheckInterval.TotalSeconds);

        // Stagger so enrollment + heartbeat have a chance to start first.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var lastMs   = Interlocked.Read(ref _lastHeartbeatMs);
                var lastAt   = DateTimeOffset.FromUnixTimeMilliseconds(lastMs);
                var elapsed  = DateTimeOffset.UtcNow - lastAt;

                if (elapsed > UnresponsiveThreshold)
                {
                    _logger.LogError(
                        "WatchdogWorker: agent unresponsive for {Elapsed:F0}s " +
                        "(threshold={Threshold}s) — triggering shutdown for service recovery",
                        elapsed.TotalSeconds, UnresponsiveThreshold.TotalSeconds);

                    // Request graceful shutdown. The Windows Service manager restarts
                    // the process per its configured recovery policy.
                    _lifetime.StopApplication();
                    return;
                }

                _logger.LogDebug(
                    "WatchdogWorker: OK — last heartbeat {Elapsed:F0}s ago",
                    elapsed.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — not a watchdog trigger.
        }
    }
}
