// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using CloudSmith.Runner.Enrollment;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSmith.Runner.Tests.Enrollment;

public sealed class EnrollmentClientTests : IDisposable
{
    private readonly string _tmpDir;

    public EnrollmentClientTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "cs-agent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* ignore */ }
    }

    private EnrollmentClient MakeClient(HttpClient http, string token = "test-token") =>
        new(http,
            "http://relay.local:8080",
            token,
            NullLogger<EnrollmentClient>.Instance,
            Path.Combine(_tmpDir, "agent-identity.json"));

    [Fact]
    public async Task EnsureEnrolledAsync_FirstBoot_PostsAndPersists()
    {
        // Arrange
        string? capturedBody = null;
        Uri? capturedUri = null;

        var handler = new StubHandler(async (req, ct) =>
        {
            capturedUri = req.RequestUri;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"agentId":"agent-abc-123","agentSecret":"super-secret-42"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var client = MakeClient(http);

        // Act
        var identity = await client.EnsureEnrolledAsync(CancellationToken.None);

        // Assert — response parsed
        Assert.Equal("agent-abc-123", identity.AgentId);
        Assert.Equal("super-secret-42", identity.AgentSecret);
        Assert.Equal("http://relay.local:8080", identity.RelayUrl);

        // Endpoint shape
        Assert.NotNull(capturedUri);
        Assert.Equal("http://relay.local:8080/lan/v1/agents/enroll", capturedUri!.AbsoluteUri);

        // Body carried enrollmentToken + hostInfo
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("test-token", doc.RootElement.GetProperty("enrollmentToken").GetString());
        var hostInfo = doc.RootElement.GetProperty("hostInfo");
        Assert.NotNull(hostInfo.GetProperty("computerName").GetString());

        // Identity file persisted
        var idPath = Path.Combine(_tmpDir, "agent-identity.json");
        Assert.True(File.Exists(idPath), "agent-identity.json not persisted");
    }

    [Fact]
    public async Task EnsureEnrolledAsync_ExistingIdentity_DoesNotCallRelay()
    {
        // Arrange — pre-seed identity file
        var idPath = Path.Combine(_tmpDir, "agent-identity.json");
        var existingIdentity = new AgentIdentity(
            AgentId:       "existing-agent",
            AgentSecret:   "existing-secret",
            RelayUrl:      "http://relay.local:8080",
            EnrolledAtUtc: DateTimeOffset.UtcNow.AddHours(-1));

        await File.WriteAllTextAsync(idPath,
            JsonSerializer.Serialize(existingIdentity, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var callCount = 0;
        var handler = new StubHandler((_, _) => { callCount++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        using var http = new HttpClient(handler);

        var client = new EnrollmentClient(
            http,
            "http://relay.local:8080",
            "any-token",
            NullLogger<EnrollmentClient>.Instance,
            idPath);

        // Act
        var identity = await client.EnsureEnrolledAsync(CancellationToken.None);

        // Assert — loaded from disk, no HTTP call made
        Assert.Equal("existing-agent", identity.AgentId);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task EnsureEnrolledAsync_ServerError_ThrowsInvalidOperation()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid token", Encoding.UTF8, "text/plain"),
        }));
        using var http = new HttpClient(handler);
        var client = MakeClient(http);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnsureEnrolledAsync(CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => impl(request, cancellationToken);
    }
}
