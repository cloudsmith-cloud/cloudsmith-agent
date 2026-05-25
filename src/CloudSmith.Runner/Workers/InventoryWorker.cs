// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner.Inventory;
using CloudSmith.Runner.Pushers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSmith.Runner.Workers;

/// <summary>
/// Periodic inventory scan worker.
///
/// Runs a local Hyper-V scan every <see cref="AgentOptions.ScanInterval"/> and
/// pushes the result to the Relay LAN listener via <see cref="RelayPusher"/>.
///
/// The scan uses a LOCAL PowerShell runspace (no WinRM / PSRemote) — the Agent
/// runs ON the Hyper-V host and invokes cmdlets in-process.
/// </summary>
public sealed class InventoryWorker : BackgroundService
{
    private readonly AgentOptions _opts;
    private readonly HyperVScanner _scanner;
    private readonly HardwareScanner _hardwareScanner;
    private readonly RelayPusher _pusher;
    private readonly ILogger<InventoryWorker> _logger;

    public InventoryWorker(
        IOptions<AgentOptions> opts,
        HyperVScanner scanner,
        HardwareScanner hardwareScanner,
        RelayPusher pusher,
        ILogger<InventoryWorker> logger)
    {
        _opts            = opts.Value;
        _scanner         = scanner;
        _hardwareScanner = hardwareScanner;
        _pusher          = pusher;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "InventoryWorker starting — scan every {Interval}s",
            _opts.ScanIntervalSeconds);

        // Stagger first scan briefly so enrollment completes first.
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opts.ScanIntervalSeconds));
        try
        {
            do
            {
                await ScanAndPushAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    private async Task ScanAndPushAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Inventory scan starting");
            var vms = await _scanner.ScanAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Inventory scan complete: {Count} VM(s)", vms.Count);

            await _pusher.PushInventoryAsync(_opts.ClusterId, vms, ct).ConfigureAwait(false);

            // Hardware scan — runs on same interval, pushed separately.
            var hardware = await _hardwareScanner.ScanAsync(ct).ConfigureAwait(false);
            await _pusher.PushHardwareAsync(hardware, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory scan/push cycle failed — will retry next interval");
        }
    }
}
