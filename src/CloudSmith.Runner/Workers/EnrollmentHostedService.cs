// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Runner.Enrollment;
using CloudSmith.Runner.Pushers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Runner.Workers;

/// <summary>
/// Startup hosted service that ensures the Agent is enrolled before the
/// heartbeat and inventory workers begin.
///
/// Order guarantee: <see cref="IHostedService"/> implementations start in the
/// DI registration order. EnrollmentHostedService is registered first in Program.cs
/// so it completes enrollment synchronously (within StartAsync) before other services
/// receive their StartAsync call.
///
/// After enrollment the resulting identity is injected into <see cref="RelayPusher"/>
/// so all subsequent HTTP calls carry the correct agentId + secret.
/// </summary>
public sealed class EnrollmentHostedService : IHostedService
{
    private readonly EnrollmentClient _enrollmentClient;
    private readonly RelayPusher _pusher;
    private readonly JobWorker _jobWorker;
    private readonly WatchdogWorker _watchdog;
    private readonly ILogger<EnrollmentHostedService> _logger;

    public EnrollmentHostedService(
        EnrollmentClient enrollmentClient,
        RelayPusher pusher,
        JobWorker jobWorker,
        WatchdogWorker watchdog,
        ILogger<EnrollmentHostedService> logger)
    {
        _enrollmentClient = enrollmentClient;
        _pusher           = pusher;
        _jobWorker        = jobWorker;
        _watchdog         = watchdog;
        _logger           = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("EnrollmentHostedService: ensuring Agent is enrolled");

        // Wire watchdog into RelayPusher so it notifies the watchdog on heartbeat success.
        _pusher.SetWatchdog(_watchdog);

        // Retry enrollment a few times at startup in case the Relay is briefly unavailable.
        const int MaxAttempts = 5;
        var delay = TimeSpan.FromSeconds(5);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var identity = await _enrollmentClient
                    .EnsureEnrolledAsync(cancellationToken)
                    .ConfigureAwait(false);

                _pusher.SetIdentity(identity);
                _jobWorker.SetIdentity(identity);
                _logger.LogInformation(
                    "Enrollment complete: agentId={AgentId}", identity.AgentId);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(ex,
                    "Enrollment attempt {Attempt}/{Max} failed — retrying in {Delay}s",
                    attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2); // exponential backoff
            }
        }

        // Final attempt — let the exception propagate and crash the host.
        var id = await _enrollmentClient
            .EnsureEnrolledAsync(cancellationToken)
            .ConfigureAwait(false);
        _pusher.SetIdentity(id);
        _jobWorker.SetIdentity(id);
        _watchdog.RecordHeartbeat();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
