// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Runner.Inventory;

/// <summary>
/// Snapshot of a single Hyper-V VM, as reported by the local host.
/// Shape mirrors the Relay's <c>VmSnapshot</c> / PaaS ingest contract.
/// </summary>
public sealed record VmSnapshot(
    [property: JsonPropertyName("vmId")] string VmId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("cpuCount")] int CpuCount,
    [property: JsonPropertyName("memoryBytes")] long MemoryBytes,
    [property: JsonPropertyName("observedAtUtc")] DateTimeOffset ObservedAtUtc);
