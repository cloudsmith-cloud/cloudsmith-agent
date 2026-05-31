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

// ---------------------------------------------------------------------------
// Agent self-update wire shapes  (AB#1951)
// ---------------------------------------------------------------------------

/// <summary>
/// Command delivered by the Relay to trigger an in-place self-update of the
/// CloudSmith Agent Windows Service binary.
/// Fetched via GET /lan/v1/agents/{agentId}/agent-update.
/// </summary>
public sealed record AgentUpdateCommand(
    [property: JsonPropertyName("updateId")]    Guid    UpdateId,
    [property: JsonPropertyName("version")]     string  Version,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl);

/// <summary>
/// Final result posted to the Relay after the agent self-update script is
/// launched (or fails to launch).
/// Posted to POST /lan/v1/agents/{agentId}/agent-update/{updateId}/result.
/// </summary>
public sealed record AgentUpdateResult(
    [property: JsonPropertyName("updateId")]  Guid    UpdateId,
    [property: JsonPropertyName("succeeded")] bool    Succeeded,
    [property: JsonPropertyName("version")]   string? Version,
    [property: JsonPropertyName("error")]     string? Error);
