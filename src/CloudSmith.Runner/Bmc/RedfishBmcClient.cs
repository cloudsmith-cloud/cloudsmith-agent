// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudSmith.Sdk.Secrets;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Bmc;

/// <summary>
/// Redfish BMC client. Implements <see cref="IBmcClient"/> using the DMTF Redfish
/// API (DSP0266). Credentials are fetched from <see cref="ICloudSmithSecretsProvider"/>
/// on every call — never cached — to ensure rotated credentials take effect immediately.
///
/// TLS:
///   - TLS 1.2 minimum enforced on the shared <see cref="HttpClient"/>.
///   - Self-signed BMC certificates: pass a dedicated <see cref="HttpClient"/> whose
///     <see cref="HttpClientHandler"/> trusts the BMC CA via <c>AGENT_BMC_CERT_PATH</c>.
///     Global certificate validation is never disabled.
///
/// Redfish session management:
///   - Uses HTTP Basic Auth per request (Redfish §7.3.2). A Redfish session token
///     would require sticky affinity which is complex in a stateless worker.
///   - Basic Auth credentials are Base64-encoded in the Authorization header per RFC 7617.
///
/// AB#1462
/// </summary>
public sealed class RedfishBmcClient : IBmcClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICloudSmithSecretsProvider _secrets;
    private readonly HttpClient                  _http;
    private readonly ILogger<RedfishBmcClient>   _logger;

    public RedfishBmcClient(
        ICloudSmithSecretsProvider secrets,
        HttpClient                  http,
        ILogger<RedfishBmcClient>   logger)
    {
        _secrets = secrets;
        _http    = http;
        _logger  = logger;
    }

    /// <inheritdoc/>
    public async Task<BmcSystemInfo> GetSystemInfoAsync(
        string bmcEndpoint,
        string credentialRef,
        string orgId,
        CancellationToken ct = default)
    {
        var cred = await FetchCredentialAsync(credentialRef, orgId, ct).ConfigureAwait(false);
        var url  = $"{bmcEndpoint.TrimEnd('/')}/redfish/v1/Systems";

        // Discover the first System entry.
        using var listResp = await SendAsync(url, cred, ct).ConfigureAwait(false);
        listResp.EnsureSuccessStatusCode();

        var list = await listResp.Content.ReadFromJsonAsync<RedfishCollection>(JsonOpts, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("CS-BMC-ERR-001: Empty response from Redfish /Systems");

        var memberUrl = list.Members?.FirstOrDefault()?["@odata.id"]?.GetString()
            ?? throw new InvalidOperationException("CS-BMC-ERR-002: No System members in Redfish /Systems");

        var systemUrl = $"{bmcEndpoint.TrimEnd('/')}{memberUrl}";
        using var sysResp = await SendAsync(systemUrl, cred, ct).ConfigureAwait(false);
        sysResp.EnsureSuccessStatusCode();

        var raw = await sysResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct)
            .ConfigureAwait(false);

        return new BmcSystemInfo(
            Id:            raw.TryGet("Id")            ?? "(unknown)",
            Name:          raw.TryGet("Name")          ?? "(unknown)",
            Manufacturer:  raw.TryGet("Manufacturer"),
            Model:         raw.TryGet("Model"),
            SerialNumber:  raw.TryGet("SerialNumber"),
            BiosVersion:   raw.TryGet("BiosVersion"),
            PowerState:    raw.TryGet("PowerState"),
            Health:        raw.TryGetNested("Status", "Health"));
    }

    /// <inheritdoc/>
    public async Task<BmcThermalInfo> GetThermalAsync(
        string bmcEndpoint,
        string credentialRef,
        string orgId,
        CancellationToken ct = default)
    {
        var cred    = await FetchCredentialAsync(credentialRef, orgId, ct).ConfigureAwait(false);
        var chassis = await DiscoverFirstChassisPathAsync(bmcEndpoint, cred, ct).ConfigureAwait(false);
        var url     = $"{bmcEndpoint.TrimEnd('/')}{chassis}/Thermal";

        using var resp = await SendAsync(url, cred, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var raw = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct).ConfigureAwait(false);

        var temps = ParseArray(raw, "Temperatures", e => new BmcTemperatureReading(
            Name:                    e.TryGet("Name")  ?? "(unknown)",
            ReadingCelsius:          e.TryGetDouble("ReadingCelsius"),
            UpperThresholdCritical:  e.TryGetDouble("UpperThresholdCritical")));

        var fans = ParseArray(raw, "Fans", e => new BmcFanReading(
            Name:         e.TryGet("Name")  ?? "(unknown)",
            Reading:      e.TryGetInt("Reading"),
            ReadingUnits: e.TryGet("ReadingUnits"),
            Health:       e.TryGetNested("Status", "Health")));

        return new BmcThermalInfo(Temperatures: temps, Fans: fans);
    }

    /// <inheritdoc/>
    public async Task<BmcPowerInfo> GetPowerAsync(
        string bmcEndpoint,
        string credentialRef,
        string orgId,
        CancellationToken ct = default)
    {
        var cred    = await FetchCredentialAsync(credentialRef, orgId, ct).ConfigureAwait(false);
        var chassis = await DiscoverFirstChassisPathAsync(bmcEndpoint, cred, ct).ConfigureAwait(false);
        var url     = $"{bmcEndpoint.TrimEnd('/')}{chassis}/Power";

        using var resp = await SendAsync(url, cred, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var raw = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct).ConfigureAwait(false);

        // PowerControl[0].PowerConsumedWatts is the total consumed power.
        double? consumed = null;
        if (raw.TryGetProperty("PowerControl", out var ctrl)
            && ctrl.ValueKind == JsonValueKind.Array
            && ctrl.GetArrayLength() > 0)
        {
            consumed = ctrl[0].TryGetDouble("PowerConsumedWatts");
        }

        var supplies = ParseArray(raw, "PowerSupplies", e => new BmcPowerSupplyInfo(
            Name:             e.TryGet("Name")    ?? "(unknown)",
            Status:           e.TryGetNested("Status", "State"),
            PowerInputWatts:  e.TryGetDouble("PowerInputWatts"),
            LineInputVoltage: e.TryGetDouble("LineInputVoltage")));

        return new BmcPowerInfo(PowerConsumedWatts: consumed, PowerSupplies: supplies);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Retrieves BMC credentials from the secrets provider. Credentials are stored as a
    /// JSON object <c>{"username":"...","password":"..."}</c> under the reference name.
    /// Never cached — called fresh on every BMC request.
    /// </summary>
    private async Task<BmcCredential> FetchCredentialAsync(
        string credentialRef,
        string orgId,
        CancellationToken ct)
    {
        _logger.LogDebug("CS-BMC-DBG-001: Fetching BMC credential {Ref} for org {OrgId}", credentialRef, orgId);
        var raw = await _secrets.GetSecretAsync(orgId, credentialRef, ct).ConfigureAwait(false);

        try
        {
            using var doc  = JsonDocument.Parse(raw);
            var username   = doc.RootElement.GetProperty("username").GetString()
                ?? throw new FormatException("CS-BMC-ERR-003: BMC credential missing 'username' field");
            var password   = doc.RootElement.GetProperty("password").GetString()
                ?? throw new FormatException("CS-BMC-ERR-004: BMC credential missing 'password' field");
            return new BmcCredential(username, password);
        }
        catch (JsonException ex)
        {
            throw new FormatException(
                $"CS-BMC-ERR-005: BMC credential {credentialRef} is not valid JSON — expected {{\"username\":\"...\",\"password\":\"...\"}}",
                ex);
        }
    }

    /// <summary>Sends a GET request with HTTP Basic Auth credentials.</summary>
    private Task<HttpResponseMessage> SendAsync(
        string url,
        BmcCredential cred,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var encoded = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{cred.Username}:{cred.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Discovers the first Chassis @odata.id path from /redfish/v1/Chassis.</summary>
    private async Task<string> DiscoverFirstChassisPathAsync(
        string bmcEndpoint,
        BmcCredential cred,
        CancellationToken ct)
    {
        var url = $"{bmcEndpoint.TrimEnd('/')}/redfish/v1/Chassis";
        using var resp = await SendAsync(url, cred, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var list = await resp.Content.ReadFromJsonAsync<RedfishCollection>(JsonOpts, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("CS-BMC-ERR-006: Empty response from Redfish /Chassis");

        return list.Members?.FirstOrDefault()?["@odata.id"]?.GetString()
            ?? throw new InvalidOperationException("CS-BMC-ERR-007: No Chassis members in Redfish /Chassis");
    }

    private static IReadOnlyList<T> ParseArray<T>(
        JsonElement root,
        string propertyName,
        Func<JsonElement, T> selector)
    {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<T>();

        var result = new List<T>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
            result.Add(selector(item));
        return result;
    }

    // Minimal Redfish collection shape — only the Members array is needed.
    private sealed class RedfishCollection
    {
        [JsonPropertyName("Members")]
        public List<Dictionary<string, JsonElement>>? Members { get; set; }
    }
}

/// <summary>JsonElement extension helpers for concise null-safe access.</summary>
internal static class JsonElementExtensions
{
    internal static string? TryGet(this JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    internal static string? TryGetNested(this JsonElement e, string outer, string inner)
        => e.TryGetProperty(outer, out var o) ? o.TryGet(inner) : null;

    internal static double? TryGetDouble(this JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : null;

    internal static int? TryGetInt(this JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : null;
}
