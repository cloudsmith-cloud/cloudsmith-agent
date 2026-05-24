// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Inventory;

/// <summary>
/// Scans the local Hyper-V host via WMI (root\virtualization\v2).
///
/// Does NOT use the PowerShell SDK or WinRM. The Agent runs ON the Hyper-V host
/// as LocalSystem and reads the Hyper-V WMI v2 namespace in-process via
/// System.Management. This is the only path that works reliably in Windows service
/// session 0 — the PowerShell SDK path deadlocks on the WinPS compatibility shim
/// that Get-VM triggers under LocalSystem.
///
/// Field mapping vs. Get-VM:
///   Msvm_ComputerSystem.ElementName           -> Name
///   Msvm_ComputerSystem.Name (GUID string)    -> VmId
///   Msvm_ComputerSystem.EnabledState (ushort) -> State
///   Msvm_ProcessorSettingData.VirtualQuantity -> CpuCount
///   Msvm_MemorySettingData.VirtualQuantity    -> MemoryBytes (value is MB; multiplied by 1024^2)
/// </summary>
public sealed class HyperVScanner
{
    private const string HyperVNamespace = @"root\virtualization\v2";

    private readonly ILogger<HyperVScanner> _logger;

    public HyperVScanner(ILogger<HyperVScanner> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<VmSnapshot>> ScanAsync(CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<VmSnapshot>>(() => ScanLocal(ct), ct);
    }

    private IReadOnlyList<VmSnapshot> ScanLocal(CancellationToken ct)
    {
        _logger.LogInformation("HyperVScanner: querying {Ns} via WMI", HyperVNamespace);

        var scope = new ManagementScope(HyperVNamespace);
        scope.Connect();

        // Caption='Virtual Machine' excludes the host itself (Caption='Hosting Computer System').
        var vmQuery = new ObjectQuery(
            "SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'");

        var snapshots = new List<VmSnapshot>();
        var now    = DateTimeOffset.UtcNow;
        var hostId = Environment.MachineName;

        using var searcher = new ManagementObjectSearcher(scope, vmQuery);
        using var vms = searcher.Get();

        foreach (ManagementObject vm in vms)
        {
            ct.ThrowIfCancellationRequested();

            using (vm)
            {
                var name  = vm["ElementName"] as string ?? "unknown";
                var vmId  = vm["Name"]        as string ?? Guid.NewGuid().ToString();
                var state = MapEnabledState(ToUInt16(vm["EnabledState"]));

                var (cpuCount, memoryBytes) = ReadVmSettingsDirect(scope, vmId, ct);

                snapshots.Add(new VmSnapshot(
                    VmId:          vmId,
                    Name:          name,
                    HostId:        hostId,
                    State:         state,
                    CpuCount:      cpuCount,
                    MemoryBytes:   memoryBytes,
                    ObservedAtUtc: now));
            }
        }

        _logger.LogInformation(
            "HyperVScanner: found {Count} VM(s) on {Host}", snapshots.Count, hostId);
        return snapshots;
    }

    // Query CPU and memory directly via WQL on the scope — avoids ManagementObject.GetRelated
    // which throws InvalidOperationException on objects returned from an enumerator.
    // InstanceID pattern: "Microsoft:{vmGuid}%" matches the active-settings objects.
    private static (int cpuCount, long memoryBytes) ReadVmSettingsDirect(
        ManagementScope scope, string vmId, CancellationToken ct)
    {
        int  cpuCount    = 0;
        long memoryBytes = 0;

        try
        {
            using var procSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT VirtualQuantity FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE 'Microsoft:{vmId}%'"));
            using var procResults = procSearcher.Get();
            foreach (ManagementObject obj in procResults)
            {
                using (obj)
                {
                    var v = ToUInt64(obj["VirtualQuantity"]);
                    if (v > 0) { cpuCount = (int)v; break; }
                }
            }
        }
        catch { /* non-fatal — cpu_count is nullable */ }

        try
        {
            using var memSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT VirtualQuantity FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmId}%'"));
            using var memResults = memSearcher.Get();
            foreach (ManagementObject obj in memResults)
            {
                using (obj)
                {
                    var mb = ToUInt64(obj["VirtualQuantity"]);
                    if (mb > 0) { memoryBytes = (long)mb * 1024L * 1024L; break; }
                }
            }
        }
        catch { /* non-fatal — memory_mb is nullable */ }

        return (cpuCount, memoryBytes);
    }

    private static string MapEnabledState(ushort state) => state switch
    {
        2     => "Running",
        3     => "Off",
        6     => "Saved",
        9     => "Paused",
        10    => "Starting",
        32773 => "Saving",
        32776 => "Pausing",
        32777 => "Resuming",
        32778 => "FastSaved",
        32779 => "FastSaving",
        _     => $"Unknown({state})",
    };

    private static ushort ToUInt16(object? v) => v switch
    {
        null     => 0,
        ushort u => u,
        short s  => (ushort)s,
        int i    => (ushort)i,
        uint ui  => (ushort)ui,
        _        => ushort.TryParse(v.ToString(), out var r) ? r : (ushort)0,
    };

    private static ulong ToUInt64(object? v) => v switch
    {
        null     => 0UL,
        ulong u  => u,
        long l   => (ulong)l,
        uint ui  => ui,
        int i    => (ulong)i,
        ushort s => s,
        _        => ulong.TryParse(v.ToString(), out var r) ? r : 0UL,
    };
}
