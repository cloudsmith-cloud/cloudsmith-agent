// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Runner.Inventory;

public sealed record HostHardwareSnapshot(
    [property: JsonPropertyName("hostId")]              string HostId,
    [property: JsonPropertyName("processorCount")]      int ProcessorCount,
    [property: JsonPropertyName("logicalCoreCount")]    int LogicalCoreCount,
    [property: JsonPropertyName("processorName")]       string? ProcessorName,
    [property: JsonPropertyName("totalMemoryBytes")]    long TotalMemoryBytes,
    [property: JsonPropertyName("networkAdapters")]     IReadOnlyList<NetworkAdapterInfo> NetworkAdapters,
    [property: JsonPropertyName("diskDrives")]          IReadOnlyList<DiskDriveInfo> DiskDrives,
    [property: JsonPropertyName("observedAtUtc")]       DateTimeOffset ObservedAtUtc);

public sealed record NetworkAdapterInfo(
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("macAddress")]   string? MacAddress,
    [property: JsonPropertyName("speedBps")]     long? SpeedBps,
    [property: JsonPropertyName("isPhysical")]   bool IsPhysical);

public sealed record DiskDriveInfo(
    [property: JsonPropertyName("model")]        string? Model,
    [property: JsonPropertyName("serialNumber")] string? SerialNumber,
    [property: JsonPropertyName("sizeBytes")]    long SizeBytes,
    [property: JsonPropertyName("mediaType")]    string? MediaType);
