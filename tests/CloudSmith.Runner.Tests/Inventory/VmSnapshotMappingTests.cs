// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner.Inventory;
using Xunit;

namespace CloudSmith.Runner.Tests.Inventory;

public sealed class VmSnapshotMappingTests
{
    [Fact]
    public void VmSnapshot_Constructor_SetsAllProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var snap = new VmSnapshot(
            VmId:          "vm-guid-123",
            Name:          "web01",
            HostId:        "HYPER-V-HOST",
            State:         "Running",
            CpuCount:      4,
            MemoryBytes:   8L * 1024 * 1024 * 1024,
            ObservedAtUtc: now);

        Assert.Equal("vm-guid-123", snap.VmId);
        Assert.Equal("web01", snap.Name);
        Assert.Equal("HYPER-V-HOST", snap.HostId);
        Assert.Equal("Running", snap.State);
        Assert.Equal(4, snap.CpuCount);
        Assert.Equal(8L * 1024 * 1024 * 1024, snap.MemoryBytes);
        Assert.Equal(now, snap.ObservedAtUtc);
    }

    [Fact]
    public void VmSnapshot_JsonRoundTrip_PreservesValues()
    {
        var now = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var snap = new VmSnapshot(
            VmId:          "vm-1",
            Name:          "db01",
            HostId:        "HOST-A",
            State:         "Off",
            CpuCount:      2,
            MemoryBytes:   4L * 1024 * 1024 * 1024,
            ObservedAtUtc: now);

        var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(snap, opts);
        var back = System.Text.Json.JsonSerializer.Deserialize<VmSnapshot>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(snap.VmId, back!.VmId);
        Assert.Equal(snap.Name, back.Name);
        Assert.Equal(snap.CpuCount, back.CpuCount);
        Assert.Equal(snap.MemoryBytes, back.MemoryBytes);
        Assert.Equal(snap.ObservedAtUtc, back.ObservedAtUtc);
    }
}
