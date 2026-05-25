// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudSmith.Runner.Enrollment;
using CloudSmith.Runner.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSmith.Runner.Workers;

/// <summary>
/// Polls the Relay for dispatched jobs, executes them via PS7 subprocess,
/// and reports results back to the Relay.
///
/// Poll interval: 10 seconds. Jobs are executed sequentially — one at a time.
/// PS7 payloads are stored under %ProgramData%\CloudSmith\payloads\.
/// </summary>
public sealed class JobWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string PayloadDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CloudSmith", "payloads");

    private readonly AgentOptions _opts;
    private readonly HttpClient _http;
    private readonly ILogger<JobWorker> _logger;

    // Set by EnrollmentHostedService after enrollment.
    private AgentIdentity? _identity;

    public JobWorker(
        IOptions<AgentOptions> opts,
        HttpClient http,
        ILogger<JobWorker> logger)
    {
        _opts   = opts.Value;
        _http   = http;
        _logger = logger;
    }

    public void SetIdentity(AgentIdentity identity) => _identity = identity;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("JobWorker starting — polling every 10s");

        // Stagger start so enrollment finishes first.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        Directory.CreateDirectory(PayloadDir);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do { await PollAndExecuteAsync(ct).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task PollAndExecuteAsync(CancellationToken ct)
    {
        if (_identity is null) return;

        var jobs = await FetchPendingJobsAsync(ct).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteJobAsync(job, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<JobDispatch>> FetchPendingJobsAsync(CancellationToken ct)
    {
        var url = $"{_identity!.RelayUrl}/lan/v1/agents/{_identity.AgentId}/jobs";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Agent-Secret", _identity.AgentSecret);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<JobDispatch>();
            var jobs = await resp.Content.ReadFromJsonAsync<List<JobDispatch>>(JsonOpts, ct).ConfigureAwait(false);
            return jobs ?? Array.Empty<JobDispatch>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "JobWorker: job poll failed");
            return Array.Empty<JobDispatch>();
        }
    }

    private async Task ExecuteJobAsync(JobDispatch job, CancellationToken ct)
    {
        _logger.LogInformation("JobWorker: executing job {JobId} type={JobType} script={Script}",
            job.JobId, job.JobType, job.Payload.ScriptName);

        var scriptPath = Path.Combine(PayloadDir, job.Payload.ScriptName);
        if (!File.Exists(scriptPath))
        {
            _logger.LogWarning("JobWorker: payload script not found: {Path}", scriptPath);
            await ReportResultAsync(new JobResult(job.JobId, false, -1, string.Empty,
                $"Payload script not found: {job.Payload.ScriptName}"), ct).ConfigureAwait(false);
            return;
        }

        // Build PS7 argument string from job payload arguments.
        var argBuilder = new StringBuilder($"-NonInteractive -NoProfile -File \"{scriptPath}\"");
        if (job.Payload.Arguments is not null)
        {
            foreach (var (key, value) in job.Payload.Arguments)
                argBuilder.Append($" -{key} \"{value.Replace("\"", "`\"")}\"");
        }

        var psi = new ProcessStartInfo
        {
            FileName               = "pwsh.exe",
            Arguments              = argBuilder.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        // Propagate W3C trace context so distributed traces correlate.
        if (job.Traceparent is not null)
            psi.Environment["TRACEPARENT"] = job.Traceparent;

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start pwsh.exe");

            // Collect stdout/stderr concurrently to avoid deadlock on large output.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            // Wait up to 30 minutes for job completion.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var exitCode = proc.ExitCode;
            var succeeded = exitCode == 0;

            _logger.LogInformation(
                "JobWorker: job {JobId} completed exitCode={ExitCode}", job.JobId, exitCode);

            await ReportResultAsync(new JobResult(job.JobId, succeeded, exitCode, stdout,
                string.IsNullOrEmpty(stderr) ? null : stderr), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("JobWorker: job {JobId} timed out or cancelled", job.JobId);
            await ReportResultAsync(new JobResult(job.JobId, false, -1, string.Empty, "Timed out"), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JobWorker: job {JobId} threw exception", job.JobId);
            await ReportResultAsync(new JobResult(job.JobId, false, -1, string.Empty, ex.Message), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task ReportResultAsync(JobResult result, CancellationToken ct)
    {
        if (_identity is null) return;
        var url = $"{_identity.RelayUrl}/lan/v1/agents/{_identity.AgentId}/jobs/{result.JobId}/result";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(result, options: JsonOpts),
        };
        req.Headers.Add("X-Agent-Secret", _identity.AgentSecret);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("JobWorker: result report HTTP {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "JobWorker: failed to report result for job {JobId}", result.JobId);
        }
    }
}
