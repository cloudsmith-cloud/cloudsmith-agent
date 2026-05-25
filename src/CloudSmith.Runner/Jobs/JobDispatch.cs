// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Runner.Jobs;

/// <summary>
/// Wire shape of a job dispatched by the Relay to this Agent.
/// Relay delivers these via GET /lan/v1/agents/{agentId}/jobs.
/// </summary>
public sealed record JobDispatch(
    [property: JsonPropertyName("jobId")]      Guid   JobId,
    [property: JsonPropertyName("jobType")]    string JobType,
    [property: JsonPropertyName("payload")]    JobPayload Payload,
    [property: JsonPropertyName("traceparent")] string? Traceparent);

public sealed record JobPayload(
    [property: JsonPropertyName("scriptName")] string ScriptName,
    [property: JsonPropertyName("arguments")]  Dictionary<string, string>? Arguments);

public sealed record JobResult(
    [property: JsonPropertyName("jobId")]     Guid   JobId,
    [property: JsonPropertyName("succeeded")] bool   Succeeded,
    [property: JsonPropertyName("exitCode")]  int    ExitCode,
    [property: JsonPropertyName("output")]    string Output,
    [property: JsonPropertyName("error")]     string? Error);
