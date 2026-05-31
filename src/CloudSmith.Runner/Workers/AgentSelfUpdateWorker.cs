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
/// Polls the Relay for pending agent self-update commands and executes them.
///
/// When the Relay delivers an <c>AgentUpdateCommand</c> via
/// GET /lan/v1/agents/{agentId}/agent-update the worker:
///
///   1. Logs "Platform update triggered — downloading version {version}".
///   2. Downloads the new agent zip from the provided URL (or from the
///      canonical GitHub Releases URL when no URL is supplied) into a temp path.
///   3. Writes an update script (<c>update-agent.ps1</c>) under
///      %ProgramData%\CloudSmith that stops the Windows Service, replaces the
///      binary directory, and restarts the service.
///   4. Launches the script via <c>powershell.exe -ExecutionPolicy Bypass</c>
///      with a hidden window so the update outlives this process.
///   5. Posts the result back to the Relay, then calls
///      <see cref="IHostApplicationLifetime.StopApplication"/> so the current
///      process exits cleanly — the update script handles the restart.
///
/// Poll interval: 30 seconds (longer than job/platform-update workers to reduce
/// noise; self-updates are rare operational events).
///
/// AB#1951
/// </summary>
public sealed class AgentSelfUpdateWorker : BackgroundService
{
    // ------------------------------------------------------------------
    // Constants
    // ------------------------------------------------------------------

    private static readonly TimeSpan PollInterval   = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialStagger = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CloudSmith");

    private static readonly string LogDir = Path.Combine(DataDir, "logs");

    // ------------------------------------------------------------------
    // Fields
    // ------------------------------------------------------------------

    private readonly AgentOptions _opts;
    private readonly HttpClient _http;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AgentSelfUpdateWorker> _logger;

    // Set by EnrollmentHostedService after enrollment.
    private AgentIdentity? _identity;

    // ------------------------------------------------------------------
    // Constructor
    // ------------------------------------------------------------------

    public AgentSelfUpdateWorker(
        IOptions<AgentOptions> opts,
        HttpClient http,
        IHostApplicationLifetime lifetime,
        ILogger<AgentSelfUpdateWorker> logger)
    {
        _opts     = opts.Value;
        _http     = http;
        _lifetime = lifetime;
        _logger   = logger;
    }

    /// <summary>Wire the enrolled identity — called by EnrollmentHostedService.</summary>
    public void SetIdentity(AgentIdentity identity) => _identity = identity;

    // ------------------------------------------------------------------
    // BackgroundService
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("AgentSelfUpdateWorker starting — poll interval {Interval}s",
            PollInterval.TotalSeconds);

        // Stagger so enrollment finishes before we start polling.
        try { await Task.Delay(InitialStagger, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Ensure data directory exists for temp files and log.
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);

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

        _logger.LogInformation(
            "AgentSelfUpdateWorker: update command received — updateId={Id} version={Version}",
            cmd.UpdateId, cmd.Version);

        await ExecuteSelfUpdateAsync(cmd, ct).ConfigureAwait(false);
    }

    private async Task<AgentUpdateCommand?> FetchPendingCommandAsync(CancellationToken ct)
    {
        var url = $"{_identity!.RelayUrl}/lan/v1/agents/{_identity.AgentId}/agent-update";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Agent-Secret", _identity.AgentSecret);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content
                .ReadFromJsonAsync<AgentUpdateCommand>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AgentSelfUpdateWorker: poll failed — Relay unreachable?");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Self-update sequence
    // ------------------------------------------------------------------

    private async Task ExecuteSelfUpdateAsync(AgentUpdateCommand cmd, CancellationToken ct)
    {
        _logger.LogInformation(
            "Platform update triggered — downloading version {Version}", cmd.Version);

        // 1. Resolve the download URL.
        var downloadUrl = ResolveDownloadUrl(cmd);

        // 2. Download new binary zip to a temp path.
        var tmpZip = Path.Combine(DataDir, $"agent-update-{cmd.Version}.zip");
        try
        {
            _logger.LogInformation(
                "AgentSelfUpdateWorker: downloading from {Url}", downloadUrl);

            using var download = await _http
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            download.EnsureSuccessStatusCode();

            await using var fs = new FileStream(
                tmpZip, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            await download.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "AgentSelfUpdateWorker: failed to download version {Version}", cmd.Version);
            await PostResultAsync(new AgentUpdateResult(
                cmd.UpdateId, false, null,
                $"Download failed: {ex.Message}"), ct).ConfigureAwait(false);
            return;
        }

        // 3. Write the update script to %ProgramData%\CloudSmith\update-agent.ps1.
        var scriptPath = Path.Combine(DataDir, "update-agent.ps1");
        var installDir = _opts.InstallDirectory;
        WriteUpdateScript(scriptPath, tmpZip, installDir, cmd.Version);

        // 4. Launch the update script (outlives this process).
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
                // Run in the data directory so relative paths in the script resolve.
                WorkingDirectory = DataDir,
            };

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start update script process");

            _logger.LogInformation(
                "AgentSelfUpdateWorker: update script launched (pid={Pid}) — agent will restart",
                proc.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentSelfUpdateWorker: failed to launch update script");
            await PostResultAsync(new AgentUpdateResult(
                cmd.UpdateId, false, null,
                $"Failed to launch update script: {ex.Message}"), ct).ConfigureAwait(false);
            return;
        }

        // 5. Report success to the Relay, then exit cleanly — the script handles the restart.
        await PostResultAsync(new AgentUpdateResult(
            cmd.UpdateId, true, cmd.Version, null), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "AgentSelfUpdateWorker: update {Id} initiated — stopping agent process for replacement",
            cmd.UpdateId);

        _lifetime.StopApplication();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private string ResolveDownloadUrl(AgentUpdateCommand cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd.DownloadUrl))
            return cmd.DownloadUrl;

        // Fall back to the canonical GitHub Releases asset URL.
        var tag      = cmd.Version.StartsWith('v') ? cmd.Version : $"v{cmd.Version}";
        var assetName = $"cloudsmith-agent-win-x64-{tag}.zip";
        return $"https://github.com/cloudsmith-cloud/cloudsmith-agent/releases/download/{tag}/{assetName}";
    }

    /// <summary>
    /// Writes the PowerShell update script that will stop the service, replace
    /// the binaries, and restart the service.  Executed by the launched child
    /// process after the current agent exits.
    /// </summary>
    private void WriteUpdateScript(
        string scriptPath, string zipPath, string installDir, string version)
    {
        var logPath = Path.Combine(LogDir, "update.log").Replace("'", "''");
        var zipPathPs = zipPath.Replace("'", "''");
        var installDirPs = installDir.Replace("'", "''");

        var script = $@"#Requires -Version 5
# CloudSmith Agent self-update script — generated by AgentSelfUpdateWorker
# Version: {version}
# Generated: {DateTimeOffset.UtcNow:O}

$ErrorActionPreference = 'Stop'
$LogPath = '{logPath}'

function Write-Log {{
    param([string]$Message)
    $line = ""[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message""
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
    Write-Host $line
}}

try {{
    Write-Log 'CloudSmith Agent update starting — version {version}'

    $ServiceName = 'CloudSmithAgent'
    $ZipPath     = '{zipPathPs}'
    $InstallDir  = '{installDirPs}'
    $BinaryName  = 'CloudSmith.Runner.exe'

    # Wait briefly for the current agent process to exit cleanly.
    Write-Log 'Waiting 5 seconds for agent process to exit...'
    Start-Sleep -Seconds 5

    # Stop the service if it is still running (defensive — agent called StopApplication).
    Write-Log 'Stopping service...'
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    # Verify the zip exists.
    if (-not (Test-Path $ZipPath)) {{
        throw ""Update zip not found: $ZipPath""
    }}

    # Extract to a staging directory, then replace install dir contents.
    $StagingDir = Join-Path $env:TEMP ""cloudsmith-agent-update-{version}""
    if (Test-Path $StagingDir) {{ Remove-Item $StagingDir -Recurse -Force }}
    New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

    Write-Log ""Extracting $ZipPath to $StagingDir""
    Expand-Archive -Path $ZipPath -DestinationPath $StagingDir -Force

    # Verify the binary is present in the staging dir.
    $NewBinary = Join-Path $StagingDir $BinaryName
    if (-not (Test-Path $NewBinary)) {{
        throw ""Binary not found in extracted archive: $BinaryName""
    }}

    Write-Log ""Replacing contents of $InstallDir""
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    # Remove old files before copying new ones.
    Get-ChildItem -Path $InstallDir | Remove-Item -Recurse -Force
    Get-ChildItem -Path $StagingDir | Copy-Item -Destination $InstallDir -Recurse -Force

    # Clean up staging dir and downloaded zip.
    Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $ZipPath    -Force          -ErrorAction SilentlyContinue

    Write-Log 'Starting service...'
    Start-Service -Name $ServiceName

    $Svc = Get-Service -Name $ServiceName
    Write-Log ""Service status: $($Svc.Status)""

    if ($Svc.Status -eq 'Running') {{
        Write-Log ""CloudSmith Agent updated to version {version} and running.""
    }} else {{
        Write-Log ""WARNING: Service did not reach Running state after update.""
    }}
}}
catch {{
    $msg = ""ERROR during update: $_""
    try {{ Add-Content -Path $LogPath -Value ""[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"" -Encoding UTF8 }} catch {{ }}
    Write-Error $msg
    exit 1
}}
";
        File.WriteAllText(scriptPath, script, System.Text.Encoding.UTF8);
        _logger.LogDebug("AgentSelfUpdateWorker: update script written to {Path}", scriptPath);
    }

    // ------------------------------------------------------------------
    // Relay HTTP helper
    // ------------------------------------------------------------------

    private async Task PostResultAsync(AgentUpdateResult result, CancellationToken ct)
    {
        if (_identity is null) return;
        var url = $"{_identity.RelayUrl}/lan/v1/agents/{_identity.AgentId}" +
                  $"/agent-update/{result.UpdateId}/result";

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
                    "AgentSelfUpdateWorker: result post HTTP {Status}", (int)resp.StatusCode);
            else
                _logger.LogInformation(
                    "AgentSelfUpdateWorker: result posted — updateId={Id} succeeded={Ok}",
                    result.UpdateId, result.Succeeded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "AgentSelfUpdateWorker: failed to post result for update {Id}", result.UpdateId);
        }
    }
}
