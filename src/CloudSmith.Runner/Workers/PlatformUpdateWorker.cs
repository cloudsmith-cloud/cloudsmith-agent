// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudSmith.Runner.Enrollment;
using CloudSmith.Runner.Update;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSmith.Runner.Workers;

/// <summary>
/// Polls the Relay for pending platform update commands and executes them.
///
/// On receiving a <c>platform:update</c> command the worker:
///   1. Saves the current image tag as <c>previousVersion</c> in the update-state file.
///   2. Runs <c>docker compose pull</c> then <c>docker compose up -d --no-deps</c> in
///      <see cref="AgentOptions.InstallDirectory"/>, streaming stdout/stderr back as
///      progress events.
///   3. Polls <c>GET http://localhost:5000/health</c> (or the configured API URL) for
///      up to 60 seconds.
///   4. On health-check success — posts <c>platform:update:complete</c>.
///      On timeout — rolls back to <c>previousVersion</c> and posts
///      <c>platform:update:failed</c>.
///
/// On receiving a <c>platform:update:rollback</c> command the worker reads
/// <c>previousVersion</c> from the state file and re-runs <c>up -d</c>.
///
/// Poll interval: 15 seconds. State file: %ProgramData%\CloudSmith\update-state.json.
///
/// AB#1954
/// </summary>
public sealed class PlatformUpdateWorker : BackgroundService
{
    // ------------------------------------------------------------------
    // Constants
    // ------------------------------------------------------------------

    private static readonly TimeSpan PollInterval      = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InitialStagger    = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DockerTimeout      = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CloudSmith", "update-state.json");

    // ------------------------------------------------------------------
    // Fields
    // ------------------------------------------------------------------

    private readonly AgentOptions _opts;
    private readonly HttpClient _http;
    private readonly ILogger<PlatformUpdateWorker> _logger;

    // Set by EnrollmentHostedService after enrollment.
    private AgentIdentity? _identity;

    // ------------------------------------------------------------------
    // Constructor
    // ------------------------------------------------------------------

    public PlatformUpdateWorker(
        IOptions<AgentOptions> opts,
        HttpClient http,
        ILogger<PlatformUpdateWorker> logger)
    {
        _opts   = opts.Value;
        _http   = http;
        _logger = logger;
    }

    /// <summary>Wire the enrolled identity — called by EnrollmentHostedService.</summary>
    public void SetIdentity(AgentIdentity identity) => _identity = identity;

    // ------------------------------------------------------------------
    // BackgroundService
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("PlatformUpdateWorker starting — poll interval {Interval}s",
            PollInterval.TotalSeconds);

        // Stagger so enrollment finishes before we start polling.
        try { await Task.Delay(InitialStagger, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Ensure the ProgramData directory exists for the state file.
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do { await PollAndHandleAsync(ct).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // ------------------------------------------------------------------
    // Poll
    // ------------------------------------------------------------------

    private async Task PollAndHandleAsync(CancellationToken ct)
    {
        if (_identity is null) return;

        var cmd = await FetchPendingCommandAsync(ct).ConfigureAwait(false);
        if (cmd is null) return;

        if (cmd.IsRollback)
        {
            _logger.LogInformation(
                "PlatformUpdateWorker: rollback command received — updateId={Id}", cmd.UpdateId);
            await ExecuteRollbackAsync(cmd.UpdateId, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "PlatformUpdateWorker: update command received — updateId={Id} imageTag={Tag}",
                cmd.UpdateId, cmd.ImageTag);
            await ExecuteUpdateAsync(cmd, ct).ConfigureAwait(false);
        }
    }

    private async Task<PlatformUpdateCommand?> FetchPendingCommandAsync(CancellationToken ct)
    {
        var url = $"{_identity!.RelayUrl}/lan/v1/agents/{_identity.AgentId}/platform-update";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Agent-Secret", _identity.AgentSecret);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content
                .ReadFromJsonAsync<PlatformUpdateCommand>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PlatformUpdateWorker: poll failed — Relay unreachable?");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Update sequence
    // ------------------------------------------------------------------

    private async Task ExecuteUpdateAsync(PlatformUpdateCommand cmd, CancellationToken ct)
    {
        // 1. Save current version to state file before overwriting anything.
        var previousVersion = ReadPreviousVersion();
        WriteUpdateState(cmd.ImageTag);

        // 2. Pull new image.
        var pullOk = await RunDockerComposeAsync(
            cmd.UpdateId, "pull", ct).ConfigureAwait(false);

        if (!pullOk)
        {
            // Pull failed — restore state and report failure.
            WriteUpdateState(previousVersion);
            await PostResultAsync(new PlatformUpdateResult(
                cmd.UpdateId, false, null,
                "docker compose pull failed — see progress log"), ct).ConfigureAwait(false);
            return;
        }

        // 3. Restart containers.
        var upOk = await RunDockerComposeAsync(
            cmd.UpdateId, "up -d --no-deps", ct).ConfigureAwait(false);

        if (!upOk)
        {
            await RollbackAsync(cmd.UpdateId, previousVersion, ct).ConfigureAwait(false);
            await PostResultAsync(new PlatformUpdateResult(
                cmd.UpdateId, false, null,
                "docker compose up failed — rolled back to previous version"), ct).ConfigureAwait(false);
            return;
        }

        // 4. Wait for health check.
        var healthy = await WaitForHealthAsync(ct).ConfigureAwait(false);
        if (healthy)
        {
            _logger.LogInformation(
                "PlatformUpdateWorker: update {Id} succeeded — new version {Tag}",
                cmd.UpdateId, cmd.ImageTag);
            await PostResultAsync(new PlatformUpdateResult(
                cmd.UpdateId, true, cmd.ImageTag, null), ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning(
                "PlatformUpdateWorker: health check failed after update {Id} — rolling back",
                cmd.UpdateId);
            await RollbackAsync(cmd.UpdateId, previousVersion, ct).ConfigureAwait(false);
            await PostResultAsync(new PlatformUpdateResult(
                cmd.UpdateId, false, null,
                $"Health check failed — rolled back to {previousVersion ?? "previous version"}"), ct)
                .ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Rollback
    // ------------------------------------------------------------------

    private async Task ExecuteRollbackAsync(Guid updateId, CancellationToken ct)
    {
        var previousVersion = ReadPreviousVersion();
        if (previousVersion is null)
        {
            _logger.LogWarning(
                "PlatformUpdateWorker: rollback {Id} requested but no previousVersion in state file",
                updateId);
            await PostResultAsync(new PlatformUpdateResult(
                updateId, false, null,
                "No previous version recorded — cannot rollback"), ct).ConfigureAwait(false);
            return;
        }

        await RollbackAsync(updateId, previousVersion, ct).ConfigureAwait(false);
        await PostResultAsync(new PlatformUpdateResult(
            updateId, true, previousVersion, null), ct).ConfigureAwait(false);
    }

    private async Task RollbackAsync(Guid updateId, string? previousVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            _logger.LogWarning(
                "PlatformUpdateWorker: no previousVersion available for rollback on update {Id}",
                updateId);
            return;
        }

        _logger.LogInformation(
            "PlatformUpdateWorker: rolling back update {Id} to {Version}",
            updateId, previousVersion);

        // Set IMAGE_TAG env var so compose picks up the previous tag, then restart.
        // Alternatively the compose file uses the saved tag directly.
        await RunDockerComposeAsync(
            updateId, "up -d --no-deps", ct,
            extraEnv: new Dictionary<string, string> { ["CLOUDSMITH_IMAGE_TAG"] = previousVersion })
            .ConfigureAwait(false);

        WriteUpdateState(previousVersion);
    }

    // ------------------------------------------------------------------
    // docker compose runner
    // ------------------------------------------------------------------

    private async Task<bool> RunDockerComposeAsync(
        Guid updateId,
        string composeArgs,
        CancellationToken ct,
        Dictionary<string, string>? extraEnv = null)
    {
        var installDir = _opts.InstallDirectory;
        _logger.LogInformation(
            "PlatformUpdateWorker: docker compose {Args} in {Dir}",
            composeArgs, installDir);

        var psi = new ProcessStartInfo
        {
            FileName               = "docker",
            Arguments              = $"compose {composeArgs}",
            WorkingDirectory       = installDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start docker compose");

            // Stream output lines back to Relay while the process runs.
            var streamTask = StreamOutputAsync(updateId, proc, ct);

            using var timeout = new CancellationTokenSource(DockerTimeout);
            using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await streamTask.ConfigureAwait(false);

            var succeeded = proc.ExitCode == 0;
            if (!succeeded)
                _logger.LogWarning(
                    "PlatformUpdateWorker: docker compose {Args} exited {Code}",
                    composeArgs, proc.ExitCode);
            return succeeded;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "PlatformUpdateWorker: docker compose {Args} cancelled or timed out", composeArgs);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PlatformUpdateWorker: docker compose {Args} threw an exception", composeArgs);
            return false;
        }
    }

    // Reads stdout+stderr from the process and forwards each line as a progress event.
    private async Task StreamOutputAsync(Guid updateId, Process proc, CancellationToken ct)
    {
        // Read both streams concurrently.
        var stdoutTask = ForwardStreamAsync(updateId, proc.StandardOutput, ct);
        var stderrTask = ForwardStreamAsync(updateId, proc.StandardError, ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
    }

    private async Task ForwardStreamAsync(
        Guid updateId,
        System.IO.StreamReader reader,
        CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                _logger.LogDebug("PlatformUpdateWorker [{Id}]: {Line}", updateId, line);
                await PostProgressAsync(
                    new PlatformUpdateProgress(updateId, line), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "PlatformUpdateWorker: stream read error for update {Id}", updateId);
        }
    }

    // ------------------------------------------------------------------
    // Health check
    // ------------------------------------------------------------------

    private async Task<bool> WaitForHealthAsync(CancellationToken ct)
    {
        var healthUrl = DeriveHealthUrl();
        _logger.LogInformation(
            "PlatformUpdateWorker: polling health endpoint {Url} for up to {Timeout}s",
            healthUrl, HealthCheckTimeout.TotalSeconds);

        using var timeout = new CancellationTokenSource(HealthCheckTimeout);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            try
            {
                // Use a fresh HttpRequestMessage so we can't reuse a disposed one.
                using var req  = new HttpRequestMessage(HttpMethod.Get, healthUrl);
                using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "PlatformUpdateWorker: health check passed ({Status})", (int)resp.StatusCode);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "PlatformUpdateWorker: health probe failed — retrying");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogWarning("PlatformUpdateWorker: health check timed out after {Timeout}s",
            HealthCheckTimeout.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Derives the health check URL from the Relay URL.  The API container
    /// listens on port 5000 on the same host as the Relay (for on-prem stacks).
    /// Falls back to <c>http://localhost:5000/health</c>.
    /// </summary>
    private string DeriveHealthUrl()
    {
        // Try to build the health URL from the Relay URL host.
        if (_identity is not null
            && Uri.TryCreate(_identity.RelayUrl, UriKind.Absolute, out var relayUri))
        {
            return $"{relayUri.Scheme}://{relayUri.Host}:5000/health";
        }

        return "http://localhost:5000/health";
    }

    // ------------------------------------------------------------------
    // State file
    // ------------------------------------------------------------------

    private static string? ReadPreviousVersion()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return null;
            var json  = File.ReadAllText(StateFilePath);
            var state = JsonSerializer.Deserialize<UpdateState>(json, JsonOpts);
            return state?.PreviousVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteUpdateState(string? imageTag)
    {
        try
        {
            var state = new UpdateState(imageTag, DateTimeOffset.UtcNow);
            var json  = JsonSerializer.Serialize(state, JsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            File.WriteAllText(StateFilePath, json);
        }
        catch (Exception)
        {
            // Non-fatal — log only in debug; we still proceed with the update.
        }
    }

    // ------------------------------------------------------------------
    // Relay HTTP helpers
    // ------------------------------------------------------------------

    private async Task PostProgressAsync(PlatformUpdateProgress progress, CancellationToken ct)
    {
        if (_identity is null) return;
        var url = $"{_identity.RelayUrl}/lan/v1/agents/{_identity.AgentId}" +
                  $"/platform-update/{progress.UpdateId}/progress";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(progress, options: JsonOpts),
        };
        req.Headers.Add("X-Agent-Secret", _identity.AgentSecret);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogDebug(
                    "PlatformUpdateWorker: progress post HTTP {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PlatformUpdateWorker: progress post failed");
        }
    }

    private async Task PostResultAsync(PlatformUpdateResult result, CancellationToken ct)
    {
        if (_identity is null) return;
        var url = $"{_identity.RelayUrl}/lan/v1/agents/{_identity.AgentId}" +
                  $"/platform-update/{result.UpdateId}/result";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(result, options: JsonOpts),
        };
        req.Headers.Add("X-Agent-Secret", _identity.AgentSecret);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning(
                    "PlatformUpdateWorker: result post HTTP {Status}", (int)resp.StatusCode);
            else
                _logger.LogInformation(
                    "PlatformUpdateWorker: result posted — updateId={Id} succeeded={Ok}",
                    result.UpdateId, result.Succeeded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "PlatformUpdateWorker: failed to post result for update {Id}", result.UpdateId);
        }
    }
}
