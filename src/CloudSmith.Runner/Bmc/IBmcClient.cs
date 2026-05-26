// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Runner.Bmc;

/// <summary>
/// Typed client for Baseboard Management Controller (BMC) operations via the
/// DMTF Redfish API. Credentials are fetched fresh from the secrets provider
/// on every request to ensure rotated credentials are always used.
///
/// ADR-012 — runner connectivity.
/// AB#1462 — Implement BMC Redfish client with per-request credential retrieval.
/// </summary>
public interface IBmcClient
{
    /// <summary>
    /// Returns a summary of the system managed by this BMC endpoint.
    /// Corresponds to GET /redfish/v1/Systems/{SystemId}.
    /// </summary>
    Task<BmcSystemInfo> GetSystemInfoAsync(
        string bmcEndpoint,
        string credentialRef,
        string orgId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the thermal sensors (temperatures and fans) for the BMC endpoint.
    /// Corresponds to GET /redfish/v1/Chassis/{ChassisId}/Thermal.
    /// </summary>
    Task<BmcThermalInfo> GetThermalAsync(
        string bmcEndpoint,
        string credentialRef,
        string orgId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns current power readings and power supply state.
    /// Corresponds to GET /redfish/v1/Chassis/{ChassisId}/Power.
    /// </summary>
    Task<BmcPowerInfo> GetPowerAsync(
        string bmcEndpoint,
        string credentialRef,
        string orgId,
        CancellationToken ct = default);
}
