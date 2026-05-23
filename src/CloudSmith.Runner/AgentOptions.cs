// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Runner;

/// <summary>
/// Top-level Agent process configuration, populated from environment variables
/// (set in the Windows Service registry or in the companion config file).
///
/// Environment variable names:
///   AGENT_RELAY_URL              — required; http://{relay-lan-ip}:8080
///   AGENT_ENROLLMENT_TOKEN       — required on first boot; ignored once enrolled
///   AGENT_SCAN_INTERVAL_SECONDS  — default 300 (5 minutes)
///   AGENT_CLUSTER_ID             — cluster identifier reported in inventory (default "default")
///   AGENT_IDENTITY_PATH          — path to identity file (default C:\ProgramData\CloudSmith\agent-identity.json)
/// </summary>
public sealed class AgentOptions
{
    /// <summary>Relay LAN listener base URL — AGENT_RELAY_URL.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>Enrollment token — AGENT_ENROLLMENT_TOKEN. Used only on first boot.</summary>
    public string EnrollmentToken { get; set; } = string.Empty;

    /// <summary>Inventory scan interval in seconds — AGENT_SCAN_INTERVAL_SECONDS (default 300).</summary>
    public int ScanIntervalSeconds { get; set; } = 300;

    /// <summary>Cluster identifier to include in inventory pushes — AGENT_CLUSTER_ID (default "default").</summary>
    public string ClusterId { get; set; } = "default";

    /// <summary>Path to the persisted identity file — AGENT_IDENTITY_PATH.</summary>
    public string IdentityPath { get; set; } = Enrollment.EnrollmentClient.DefaultIdentityPath;
}
