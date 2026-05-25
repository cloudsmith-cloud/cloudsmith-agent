// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Inventory;

/// <summary>
/// Scans the local host hardware via WMI (root\cimv2).
/// Collects CPU, memory, network adapters, and disk drives.
/// Runs on the Hyper-V host in-process as LocalSystem.
/// </summary>
public sealed class HardwareScanner
{
    private const string CimNamespace = @"root\cimv2";
    private readonly ILogger<HardwareScanner> _logger;

    public HardwareScanner(ILogger<HardwareScanner> logger) => _logger = logger;

    public Task<HostHardwareSnapshot> ScanAsync(CancellationToken ct) =>
        Task.Run(() => ScanLocal(ct), ct);

    private HostHardwareSnapshot ScanLocal(CancellationToken ct)
    {
        _logger.LogInformation("HardwareScanner: querying {Ns} via WMI", CimNamespace);
        var scope = new ManagementScope(CimNamespace);
        scope.Connect();

        var now    = DateTimeOffset.UtcNow;
        var hostId = Environment.MachineName;

        var (procCount, logicalCores, procName) = ScanProcessors(scope, ct);
        var totalMemory = ScanMemory(scope, ct);
        var adapters    = ScanNetworkAdapters(scope, ct);
        var disks       = ScanDiskDrives(scope, ct);

        _logger.LogInformation(
            "HardwareScanner: {Procs} CPU(s), {Mem}GB RAM, {Adapters} adapters, {Disks} disks",
            procCount, totalMemory / (1024L * 1024 * 1024), adapters.Count, disks.Count);

        return new HostHardwareSnapshot(
            hostId, procCount, logicalCores, procName,
            totalMemory, adapters, disks, now);
    }

    private static (int procCount, int logicalCores, string? procName) ScanProcessors(
        ManagementScope scope, CancellationToken ct)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"));
            using var results = searcher.Get();
            int procs = 0, cores = 0, logical = 0;
            string? name = null;
            foreach (ManagementObject obj in results)
            {
                ct.ThrowIfCancellationRequested();
                using (obj)
                {
                    procs++;
                    name ??= obj["Name"] as string;
                    cores   += Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                    logical += Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                }
            }
            return (procs, logical > 0 ? logical : cores, name?.Trim());
        }
        catch { return (0, 0, null); }
    }

    private static long ScanMemory(ManagementScope scope, CancellationToken ct)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Capacity FROM Win32_PhysicalMemory"));
            using var results = searcher.Get();
            long total = 0;
            foreach (ManagementObject obj in results)
            {
                ct.ThrowIfCancellationRequested();
                using (obj) total += Convert.ToInt64(obj["Capacity"] ?? 0L);
            }
            return total;
        }
        catch { return 0; }
    }

    private static IReadOnlyList<NetworkAdapterInfo> ScanNetworkAdapters(
        ManagementScope scope, CancellationToken ct)
    {
        var list = new List<NetworkAdapterInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    "SELECT Name, MACAddress, Speed, PhysicalAdapter FROM Win32_NetworkAdapter " +
                    "WHERE NetConnectionStatus IS NOT NULL"));
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                ct.ThrowIfCancellationRequested();
                using (obj)
                {
                    var name       = obj["Name"] as string ?? "unknown";
                    var mac        = obj["MACAddress"] as string;
                    var speedRaw   = obj["Speed"];
                    long? speed    = speedRaw is not null ? Convert.ToInt64(speedRaw) : null;
                    var isPhysical = obj["PhysicalAdapter"] is true;
                    list.Add(new NetworkAdapterInfo(name, mac, speed, isPhysical));
                }
            }
        }
        catch { /* non-fatal */ }
        return list;
    }

    private static IReadOnlyList<DiskDriveInfo> ScanDiskDrives(
        ManagementScope scope, CancellationToken ct)
    {
        var list = new List<DiskDriveInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Model, SerialNumber, Size, MediaType FROM Win32_DiskDrive"));
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                ct.ThrowIfCancellationRequested();
                using (obj)
                {
                    var model     = (obj["Model"] as string)?.Trim();
                    var serial    = (obj["SerialNumber"] as string)?.Trim();
                    var size      = Convert.ToInt64(obj["Size"] ?? 0L);
                    var mediaType = obj["MediaType"] as string;
                    list.Add(new DiskDriveInfo(model, serial, size, mediaType));
                }
            }
        }
        catch { /* non-fatal */ }
        return list;
    }
}
