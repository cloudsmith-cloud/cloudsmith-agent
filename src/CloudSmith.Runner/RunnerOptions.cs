// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Runner;

public sealed class RunnerOptions
{
    public string ApiBaseUrl { get; set; } = "https://localhost:443";
    public string RunnerName { get; set; } = Environment.MachineName;
    public string CertThumbprint { get; set; } = string.Empty;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public string[] Capabilities { get; set; } = ["invoke-deployment", "collect-inventory", "collect-hardware"];
}
