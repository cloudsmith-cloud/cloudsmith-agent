// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Sdk.Secrets;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Bmc;

/// <summary>
/// Runner-local secrets provider that reads secrets from environment variables.
///
/// Secret names are mapped to environment variable names by uppercasing and
/// replacing non-alphanumeric characters with underscores, prefixed with
/// <c>AGENT_SECRET_</c>. For example, the credential reference
/// <c>bmc-ipmi-host1</c> maps to env var <c>AGENT_SECRET_BMC_IPMI_HOST1</c>.
///
/// This provider is intended for standalone and bundled deployment modes where
/// the agent runs as a Windows Service and secrets are injected via the service
/// environment. PaaS deployments may opt to replace this with a relay-backed
/// secrets channel in a future phase.
///
/// AB#1462 — per-request credential retrieval for BMC Redfish client.
/// </summary>
public sealed class LocalSecretsProvider : ICloudSmithSecretsProvider
{
    private const string Prefix = "AGENT_SECRET_";
    private readonly ILogger<LocalSecretsProvider> _logger;

    public LocalSecretsProvider(ILogger<LocalSecretsProvider> logger) => _logger = logger;

    /// <inheritdoc/>
    public Task<string> GetSecretAsync(string orgId, string secretName, CancellationToken ct)
    {
        var envKey = Prefix + NormalizeKey(secretName);
        var value  = Environment.GetEnvironmentVariable(envKey);

        if (string.IsNullOrEmpty(value))
        {
            _logger.LogWarning("CS-BMC-WARN-001: Secret '{Name}' not found — env var {Key} is not set or empty", secretName, envKey);
            throw new KeyNotFoundException(
                $"CS-SECRETS-ERROR-0001: Secret '{secretName}' not found. " +
                $"Set environment variable {envKey} on the CloudSmith Agent service.");
        }

        _logger.LogDebug("CS-BMC-DBG-002: Resolved secret '{Name}' from env var {Key}", secretName, envKey);
        return Task.FromResult(value);
    }

    /// <inheritdoc/>
    public Task SetSecretAsync(string orgId, string secretName, string value, CancellationToken ct)
        => throw new NotSupportedException(
            "CS-SECRETS-ERROR-0003: LocalSecretsProvider is read-only. " +
            "Use the platform UI or API to manage secrets.");

    /// <inheritdoc/>
    public Task RotateSecretAsync(string orgId, string secretName, CancellationToken ct)
        => throw new NotSupportedException(
            "CS-SECRETS-ERROR-0004: LocalSecretsProvider does not support rotation. " +
            "Update the environment variable on the CloudSmith Agent service and restart.");

    /// <inheritdoc/>
    public Task<SecretMetadata> GetSecretMetadataAsync(string orgId, string secretName, CancellationToken ct)
    {
        var envKey  = Prefix + NormalizeKey(secretName);
        var present = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envKey));

        if (!present)
            throw new KeyNotFoundException(
                $"CS-SECRETS-ERROR-0001: Secret '{secretName}' not found. " +
                $"Set environment variable {envKey} on the CloudSmith Agent service.");

        var meta = new SecretMetadata(
            Name:            secretName,
            Provider:        "local-env",
            LastRotatedAt:   null,
            RefId:           envKey,
            RotationEnabled: false,
            NextRotationAt:  null);
        return Task.FromResult(meta);
    }

    private static string NormalizeKey(string name)
        => System.Text.RegularExpressions.Regex
            .Replace(name.ToUpperInvariant(), @"[^A-Z0-9]", "_");
}
