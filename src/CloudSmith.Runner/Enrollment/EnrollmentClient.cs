// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Enrollment;

/// <summary>
/// Enrolls this Agent with the Relay LAN listener on first boot.
///
/// On first boot:
///   1. POST <c>{relayUrl}/lan/v1/agents/enroll</c> with enrollment token + host info.
///   2. Persist the returned agentId + secret to <see cref="DefaultIdentityPath"/>.
///
/// On subsequent boots:
///   Load and return the persisted identity — no re-enrollment needed.
/// </summary>
public sealed class EnrollmentClient
{
    /// <summary>Default on-disk path for the persisted identity file.</summary>
    public const string DefaultIdentityPath = @"C:\ProgramData\CloudSmith\agent-identity.json";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly string _relayUrl;
    private readonly string _enrollmentToken;
    private readonly string _identityPath;
    private readonly ILogger<EnrollmentClient> _logger;

    public EnrollmentClient(
        HttpClient http,
        string relayUrl,
        string enrollmentToken,
        ILogger<EnrollmentClient> logger,
        string? identityPath = null)
    {
        _http            = http;
        _relayUrl        = relayUrl.TrimEnd('/');
        _enrollmentToken = enrollmentToken;
        _identityPath    = identityPath ?? DefaultIdentityPath;
        _logger          = logger;
    }

    /// <summary>
    /// Returns the identity from disk if already enrolled; otherwise enrolls and persists.
    /// </summary>
    public async Task<AgentIdentity> EnsureEnrolledAsync(CancellationToken ct)
    {
        var existing = TryLoadIdentity();
        if (existing is not null)
        {
            _logger.LogInformation(
                "Loaded persisted Agent identity: agentId={AgentId}", existing.AgentId);
            return existing;
        }

        _logger.LogInformation("No persisted identity — enrolling with Relay at {RelayUrl}", _relayUrl);

        var hostInfo = new
        {
            computerName = Environment.MachineName,
            ipAddresses  = GetLocalIpAddresses(),
            os           = Environment.OSVersion.ToString(),
        };

        var enrollRequest = new { enrollmentToken = _enrollmentToken, hostInfo };
        var endpoint = $"{_relayUrl}/lan/v1/agents/enroll";

        using var resp = await _http.PostAsJsonAsync(endpoint, enrollRequest, JsonOpts, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Agent enrollment failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }

        var enrolled = await resp.Content.ReadFromJsonAsync<EnrollResponse>(JsonOpts, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Enrollment response body was empty.");

        if (string.IsNullOrWhiteSpace(enrolled.AgentId))
            throw new InvalidOperationException("Enrollment response missing agentId.");

        var identity = new AgentIdentity(
            AgentId:       enrolled.AgentId,
            AgentSecret:   enrolled.AgentSecret,
            RelayUrl:      _relayUrl,
            EnrolledAtUtc: DateTimeOffset.UtcNow);

        PersistIdentity(identity);

        _logger.LogInformation("Agent enrolled: agentId={AgentId}", identity.AgentId);
        return identity;
    }

    /// <summary>Load the persisted identity (or null if not enrolled).</summary>
    public AgentIdentity? TryLoadIdentity()
    {
        if (!File.Exists(_identityPath)) return null;
        try
        {
            var json = File.ReadAllText(_identityPath);
            return JsonSerializer.Deserialize<AgentIdentity>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read identity file {Path} — will re-enroll", _identityPath);
            return null;
        }
    }

    private void PersistIdentity(AgentIdentity identity)
    {
        var dir = Path.GetDirectoryName(_identityPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(identity, JsonOpts);
        File.WriteAllText(_identityPath, json);

        // Harden the identity file so only SYSTEM and Administrators can read it.
        // Any process running as a standard user or under a compromised service
        // account is denied read access, preventing the agentSecret from being
        // recovered via a local privilege-escalation path.
        //
        // This runs as LocalSystem (the service account), so SetAccessControl
        // will succeed without elevation.
        try
        {
            ApplyRestrictiveAcl(_identityPath);
            _logger.LogInformation(
                "Agent identity persisted and ACL hardened at {Path}", _identityPath);
        }
        catch (Exception ex)
        {
            // Non-fatal: file is written; log the ACL failure so operators can
            // investigate without losing the enrolled state.
            _logger.LogWarning(ex,
                "CS-ENROLL-WARN-001: Failed to harden ACL on {Path} — identity is written but " +
                "may be readable by unprivileged processes. Re-run Install-Agent.ps1 to fix.",
                _identityPath);
        }
    }

    /// <summary>
    /// Applies a SYSTEM+Administrators-only ACL to <paramref name="filePath"/>.
    /// Inheritance is disabled and <c>BUILTIN\Users</c> is explicitly denied to
    /// prevent low-privilege processes from reading the agentSecret.
    /// </summary>
    private static void ApplyRestrictiveAcl(string filePath)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid,        null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users  = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,        null);

        acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,    AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl,    AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(users,  FileSystemRights.ReadAndExecute, AccessControlType.Deny));

        new FileInfo(filePath).SetAccessControl(acl);
    }

    private static string[] GetLocalIpAddresses()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(Environment.MachineName)
                .Select(a => a.ToString())
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // -------------------------------------------------------------------------
    // Wire types
    // -------------------------------------------------------------------------

    internal sealed record EnrollResponse(
        [property: JsonPropertyName("agentId")] string AgentId,
        [property: JsonPropertyName("agentSecret")] string AgentSecret);
}
