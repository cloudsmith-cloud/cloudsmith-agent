// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Inventory;

/// <summary>
/// Scans the local Hyper-V host using a LOCAL PowerShell runspace.
///
/// Does NOT use WinRM / PSRemote — the Agent runs ON the Hyper-V host and
/// invokes Hyper-V cmdlets in-process. This is the primary data plane per
/// ADR-007 (Agent path). PSRemote is the Relay-side fallback for hosts that
/// have not yet installed the Agent.
///
/// Requires the Agent process to run as LocalSystem (default) or a user with
/// Hyper-V Administrator rights. The <c>Microsoft.PowerShell.SDK</c> NuGet
/// package bundles the PowerShell runtime so no external PS installation is needed.
/// </summary>
public sealed class HyperVScanner
{
    private readonly ILogger<HyperVScanner> _logger;

    public HyperVScanner(ILogger<HyperVScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run <c>Get-VM</c> on the local host and return a <see cref="VmSnapshot"/>
    /// for every discovered virtual machine.
    /// </summary>
    public Task<IReadOnlyList<VmSnapshot>> ScanAsync(CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<VmSnapshot>>(() => ScanLocal(), ct);
    }

    private IReadOnlyList<VmSnapshot> ScanLocal()
    {
        _logger.LogInformation("HyperVScanner: running local Get-VM");

        // Open a local runspace — no WSMan, no network hop.
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Get-VM");
        var results = ps.Invoke();

        if (ps.HadErrors)
        {
            foreach (var err in ps.Streams.Error)
                _logger.LogWarning("Get-VM error: {Err}", err?.ToString());
        }

        var snapshots = new List<VmSnapshot>(results.Count);
        var now = DateTimeOffset.UtcNow;
        var hostId = Environment.MachineName;

        foreach (var vm in results)
        {
            if (vm?.BaseObject is null) continue;

            var name     = GetString(vm, "Name")   ?? GetString(vm, "VMName") ?? "unknown";
            var vmGuid   = GetString(vm, "VMId")   ?? GetString(vm, "Id")     ?? Guid.NewGuid().ToString();
            var state    = GetString(vm, "State")  ?? "Unknown";
            var cpuCount = GetInt(vm, "ProcessorCount") ?? 0;
            var memBytes = GetLong(vm, "MemoryAssigned") ?? GetLong(vm, "MemoryStartup") ?? 0L;

            snapshots.Add(new VmSnapshot(
                VmId:          vmGuid,
                Name:          name,
                HostId:        hostId,
                State:         state,
                CpuCount:      cpuCount,
                MemoryBytes:   memBytes,
                ObservedAtUtc: now));
        }

        _logger.LogInformation("HyperVScanner: found {Count} VM(s) on {Host}", snapshots.Count, hostId);
        return snapshots;
    }

    private static string? GetString(PSObject obj, string name)
        => obj.Properties[name]?.Value?.ToString();

    private static int? GetInt(PSObject obj, string name)
    {
        var v = obj.Properties[name]?.Value;
        return v switch
        {
            null   => null,
            int i  => i,
            long l => (int)l,
            _      => int.TryParse(v.ToString(), out var r) ? r : null,
        };
    }

    private static long? GetLong(PSObject obj, string name)
    {
        var v = obj.Properties[name]?.Value;
        return v switch
        {
            null   => null,
            long l => l,
            int i  => (long)i,
            _      => long.TryParse(v.ToString(), out var r) ? r : null,
        };
    }
}
