// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudSmith.Runner.Enrollment;
using CloudSmith.Runner.Inventory;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Pushers;

/// <summary>
/// HTTP client that posts inventory and health frames to the Relay LAN listener
/// at <c>AGENT_RELAY_URL</c>.
///
/// Auth: <c>X-Agent-Secret</c> header carrying the per-agent secret issued at enrollment.
/// </summary>
public sealed class RelayPusher
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<RelayPusher> _logger;

    // Set after enrollment (call SetIdentity before pushing).
    private AgentIdentity? _identity;

    public RelayPusher(HttpClient http, ILogger<RelayPusher> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>
    /// Bind the enrolled identity so subsequent calls know the relay URL and secret.
    /// </summary>
    public void SetIdentity(AgentIdentity identity) => _identity = identity;

    /// <summary>
    /// POST heartbeat to <c>{relayUrl}/lan/v1/agents/{agentId}/heartbeat</c>.
    /// </summary>
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        EnsureIdentity();
        var url = $"{_identity!.RelayUrl}/lan/v1/agents/{_identity.AgentId}/heartbeat";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-Agent-Secret", _identity.AgentSecret);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Heartbeat HTTP {Status}", (int)resp.StatusCode);
            else
                _logger.LogDebug("Heartbeat ack from Relay");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Heartbeat failed — Relay unreachable?");
        }
    }

    /// <summary>
    /// POST inventory to <c>{relayUrl}/lan/v1/agents/{agentId}/inventory</c>.
    /// </summary>
    public async Task PushInventoryAsync(
        string clusterId,
        IReadOnlyList<VmSnapshot> vms,
        CancellationToken ct)
    {
        EnsureIdentity();
        var url = $"{_identity!.RelayUrl}/lan/v1/agents/{_identity.AgentId}/inventory";

        var body = new InventoryPayload(clusterId, vms);
        using var req = BuildRequest(HttpMethod.Post, url, body);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Inventory push HTTP {Status}", (int)resp.StatusCode);
            else
                _logger.LogInformation("Inventory pushed: cluster={Cluster} vms={Count}", clusterId, vms.Count);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Inventory push failed — Relay unreachable?");
        }
    }

    /// <summary>
    /// POST health to <c>{relayUrl}/lan/v1/agents/{agentId}/health</c>.
    /// </summary>
    public async Task PushHealthAsync(
        string clusterId,
        string status,
        CancellationToken ct)
    {
        EnsureIdentity();
        var url = $"{_identity!.RelayUrl}/lan/v1/agents/{_identity.AgentId}/health";

        var body = new HealthPayload(clusterId, status, new List<HealthCheckItem>());
        using var req = BuildRequest(HttpMethod.Post, url, body);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Health push HTTP {Status}", (int)resp.StatusCode);
            else
                _logger.LogInformation("Health pushed: cluster={Cluster} status={Status}", clusterId, status);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Health push failed — Relay unreachable?");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private HttpRequestMessage BuildRequest<T>(HttpMethod method, string url, T body)
    {
        var req     = new HttpRequestMessage(method, url);
        req.Content = JsonContent.Create(body, options: JsonOpts);
        req.Headers.Add("X-Agent-Secret", _identity!.AgentSecret);
        return req;
    }

    private void EnsureIdentity()
    {
        if (_identity is null)
            throw new InvalidOperationException("RelayPusher: identity not set — call SetIdentity after enrollment.");
    }

    // -------------------------------------------------------------------------
    // Wire payloads (mirrors Relay LAN controller's request shapes)
    // -------------------------------------------------------------------------

    /// <summary>
    /// POST hardware snapshot to <c>{relayUrl}/lan/v1/agents/{agentId}/hardware</c>.
    /// </summary>
    public async Task PushHardwareAsync(HostHardwareSnapshot hardware, CancellationToken ct)
    {
        EnsureIdentity();
        var url = $"{_identity!.RelayUrl}/lan/v1/agents/{_identity.AgentId}/hardware";
        using var req = BuildRequest(HttpMethod.Post, url, hardware);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Hardware push HTTP {Status}", (int)resp.StatusCode);
            else
                _logger.LogInformation("Hardware pushed: host={Host}", hardware.HostId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Hardware push failed — Relay unreachable?");
        }
    }

    private sealed record InventoryPayload(
        [property: JsonPropertyName("clusterId")] string ClusterId,
        [property: JsonPropertyName("vms")] IReadOnlyList<VmSnapshot> Vms);

    private sealed record HealthPayload(
        [property: JsonPropertyName("clusterId")] string ClusterId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("checks")] IReadOnlyList<HealthCheckItem> Checks);

    private sealed record HealthCheckItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("detail")] string? Detail);
}
