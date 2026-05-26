// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Runner.Bmc;

/// <summary>System identity returned from GET /redfish/v1/Systems/{id}.</summary>
public sealed record BmcSystemInfo(
    [property: JsonPropertyName("id")]            string  Id,
    [property: JsonPropertyName("name")]          string  Name,
    [property: JsonPropertyName("manufacturer")]  string? Manufacturer,
    [property: JsonPropertyName("model")]         string? Model,
    [property: JsonPropertyName("serialNumber")]  string? SerialNumber,
    [property: JsonPropertyName("biosVersion")]   string? BiosVersion,
    [property: JsonPropertyName("powerState")]    string? PowerState,
    [property: JsonPropertyName("health")]        string? Health);

/// <summary>Thermal reading from GET /redfish/v1/Chassis/{id}/Thermal.</summary>
public sealed record BmcTemperatureReading(
    [property: JsonPropertyName("name")]                    string  Name,
    [property: JsonPropertyName("readingCelsius")]          double? ReadingCelsius,
    [property: JsonPropertyName("upperThresholdCritical")]  double? UpperThresholdCritical);

/// <summary>Fan reading from GET /redfish/v1/Chassis/{id}/Thermal.</summary>
public sealed record BmcFanReading(
    [property: JsonPropertyName("name")]             string  Name,
    [property: JsonPropertyName("reading")]          int?    Reading,
    [property: JsonPropertyName("readingUnits")]     string? ReadingUnits,
    [property: JsonPropertyName("health")]           string? Health);

/// <summary>Aggregated thermal data from BMC thermal endpoint.</summary>
public sealed record BmcThermalInfo(
    IReadOnlyList<BmcTemperatureReading> Temperatures,
    IReadOnlyList<BmcFanReading>         Fans);

/// <summary>Power reading from GET /redfish/v1/Chassis/{id}/Power.</summary>
public sealed record BmcPowerSupplyInfo(
    [property: JsonPropertyName("name")]               string  Name,
    [property: JsonPropertyName("status")]             string? Status,
    [property: JsonPropertyName("powerInputWatts")]    double? PowerInputWatts,
    [property: JsonPropertyName("lineInputVoltage")]   double? LineInputVoltage);

/// <summary>Aggregated power data from BMC power endpoint.</summary>
public sealed record BmcPowerInfo(
    [property: JsonPropertyName("powerConsumedWatts")]  double?                         PowerConsumedWatts,
    IReadOnlyList<BmcPowerSupplyInfo>                   PowerSupplies);

/// <summary>
/// BMC credential pair — retrieved from the secrets provider fresh per request.
/// Never cached beyond the scope of a single request to ensure rotated credentials
/// are picked up immediately.
/// </summary>
internal sealed record BmcCredential(string Username, string Password);
