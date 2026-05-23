// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Runner.Enrollment;

/// <summary>
/// Persisted Agent identity — what we know after a successful enrollment.
/// Serialized to <c>C:\ProgramData\CloudSmith\agent-identity.json</c>.
/// </summary>
public sealed record AgentIdentity(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("agentSecret")] string AgentSecret,
    [property: JsonPropertyName("relayUrl")] string RelayUrl,
    [property: JsonPropertyName("enrolledAtUtc")] DateTimeOffset EnrolledAtUtc);
