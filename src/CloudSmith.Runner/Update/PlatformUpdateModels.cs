// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Runner.Update;

/// <summary>
/// Wire shape of a platform update command delivered by the Relay to this Agent.
/// Relay delivers these via GET /lan/v1/agents/{agentId}/platform-update.
/// </summary>
public sealed record PlatformUpdateCommand(
    [property: JsonPropertyName("updateId")]  Guid   UpdateId,
    [property: JsonPropertyName("imageTag")]  string ImageTag,
    [property: JsonPropertyName("isRollback")] bool   IsRollback);

/// <summary>
/// Progress event posted back to the Relay during an update sequence.
/// Posted to POST /lan/v1/agents/{agentId}/platform-update/{updateId}/progress.
/// </summary>
public sealed record PlatformUpdateProgress(
    [property: JsonPropertyName("updateId")] Guid   UpdateId,
    [property: JsonPropertyName("line")]     string Line);

/// <summary>
/// Final result posted to the Relay when an update sequence finishes.
/// Posted to POST /lan/v1/agents/{agentId}/platform-update/{updateId}/result.
/// </summary>
public sealed record PlatformUpdateResult(
    [property: JsonPropertyName("updateId")]   Guid   UpdateId,
    [property: JsonPropertyName("succeeded")]  bool   Succeeded,
    [property: JsonPropertyName("newVersion")] string? NewVersion,
    [property: JsonPropertyName("error")]      string? Error);

/// <summary>
/// Persisted update state written to %ProgramData%\CloudSmith\update-state.json.
/// Stores the running image tag so rollback can restore it.
/// </summary>
public sealed record UpdateState(
    [property: JsonPropertyName("previousVersion")] string? PreviousVersion,
    [property: JsonPropertyName("updatedAtUtc")]    DateTimeOffset UpdatedAtUtc);
