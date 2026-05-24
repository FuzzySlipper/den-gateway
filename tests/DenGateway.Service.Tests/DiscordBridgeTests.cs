using DenGateway.Service.DiscordBridge;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DenGateway.Service.Tests;

public class DiscordBridgeNotificationServiceTests
{
    private static readonly DiscordBridgeOptions TestOptions = new()
    {
        Enabled = true,
        BotToken = "test-bot-token-12345",
        CooldownSeconds = 30,
        MaxBodyLength = 2000,
        Targets = new Dictionary<string, DiscordBridgeTarget>
        {
            ["agent-test-1"] = new()
            {
                ChannelId = "111111111111111111",
                ThreadId = null,
                MentionUserId = "222222222222222222",
                WakeByMention = true
            },
            ["agent-no-mention"] = new()
            {
                ChannelId = "333333333333333333",
                ThreadId = null,
                MentionUserId = "444444444444444444",
                WakeByMention = false
            },
            ["agent-thread-target"] = new()
            {
                ChannelId = "555555555555555555",
                ThreadId = "666666666666666666",
                MentionUserId = "777777777777777777",
                WakeByMention = true
            }
        }
    };

    private static DiscordNotificationRequest CreateTestRequest(
        string? target = null,
        string? dedupeKey = null)
    {
        return new DiscordNotificationRequest(
            TargetAgentIdentity: target ?? "agent-test-1",
            Body: "This is a test notification body for unit testing purposes.",
            SourceChannelId: "source-channel-1",
            SourceMessageId: "source-msg-1",
            SourceProjectId: "project-den-test",
            Requester: "test-runner",
            Urgency: "high",
            DedupeKey: dedupeKey ?? $"test-dedupe-{Guid.NewGuid():N}",
            DryRun: null);
    }

    private static DiscordNotificationService CreateService(
        DiscordBridgeOptions? options = null,
        StubHttpMessageHandler? stubHandler = null)
    {
        var databasePath = CreateTempDatabasePath();
        var repository = new DiscordNotificationRepository(databasePath);
        repository.InitializeAsync().GetAwaiter().GetResult();

        options ??= TestOptions;

        var httpClient = stubHandler is not null
            ? new HttpClient(stubHandler) { BaseAddress = new Uri("https://discord.com") }
            : new HttpClient(new StubHttpMessageHandler("sent", "987654321098765432")) { BaseAddress = new Uri("https://discord.com") };

        var httpClientFactory = new StubHttpClientFactory(httpClient);
        var apiClient = new DiscordApiClient(httpClientFactory, Options.Create(options));

        return new DiscordNotificationService(repository, apiClient, Options.Create(options));
    }

    // ==================== TESTS ====================

    [Fact]
    public async Task UnknownTargetRejected()
    {
        // Given a request with an unknown agent identity
        var service = CreateService();
        var request = CreateTestRequest(target: "unknown-agent-nonexistent", dedupeKey: "dedupe-unknown-1");

        // When we notify
        var result = await service.NotifyAsync(request);

        // Then it should be rejected without any Discord call
        Assert.Equal("rejected", result.Status);
        Assert.Contains("unknown target", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.NotificationId);
        Assert.Null(result.DryRunPayload);
    }

    [Fact]
    public async Task DryRunRendersPayloadWithoutSending()
    {
        // Given a dry_run request
        var service = CreateService();
        var request = CreateTestRequest(dedupeKey: "dedupe-dry-run-1") with { DryRun = true };

        // When we notify
        var result = await service.NotifyAsync(request);

        // Then it should return dry_run status with payload evidence
        Assert.Equal("dry_run", result.Status);
        Assert.NotNull(result.DryRunPayload);

        var payload = result.DryRunPayload;
        Assert.Equal("111111111111111111", payload.DiscordChannelId);
        Assert.Null(payload.DiscordThreadId);
        Assert.Contains("test-runner", payload.Content);
        Assert.Contains("project-den-test", payload.Content);
        Assert.Contains("This is a test notification body", payload.Content);

        // Allowed mentions should be deliberate (target user only, no @everyone)
        Assert.Empty(payload.AllowedMentions.Parse);
        Assert.Contains("222222222222222222", payload.AllowedMentions.Users);

        // Content should contain the actual Discord mention token when WakeByMention=true
        Assert.Contains("<@222222222222222222>", payload.Content);

        // No notification record was created (dry run doesn't persist)
        Assert.Null(result.NotificationId);
    }

    [Fact]
    public async Task DryRunWithThreadTargetIncludesThreadId()
    {
        var service = CreateService();
        var request = CreateTestRequest(target: "agent-thread-target", dedupeKey: "dedupe-dry-run-thread-1") with { DryRun = true };

        var result = await service.NotifyAsync(request);

        Assert.Equal("dry_run", result.Status);
        Assert.NotNull(result.DryRunPayload);
        Assert.Equal("555555555555555555", result.DryRunPayload.DiscordChannelId);
        Assert.Equal("666666666666666666", result.DryRunPayload.DiscordThreadId);

        // Content should contain the mention token for the thread target
        Assert.Contains("<@777777777777777777>", result.DryRunPayload.Content);
    }

    [Fact]
    public async Task DuplicateDedupeKeyReturnsDedupedNoSecondSend()
    {
        // Given a request with a specific dedupe key
        var handler = new StubHttpMessageHandler("sent", "first-message-id-1");
        var service = CreateService(stubHandler: handler);
        var dedupeKey = $"dedupe-dup-{Guid.NewGuid():N}";
        var request = CreateTestRequest(dedupeKey: dedupeKey);

        // When we send the first request
        var firstResult = await service.NotifyAsync(request);

        // Then the first request is sent
        Assert.Equal("sent", firstResult.Status);
        Assert.NotNull(firstResult.NotificationId);
        Assert.Equal("sent", handler.LastStatus);

        // When we send a second identical request (same dedupe key)
        handler.Reset();
        var secondResult = await service.NotifyAsync(request);

        // Then the second request is deduped (no Discord send)
        Assert.Equal("deduped", secondResult.Status);
        Assert.True(secondResult.Deduped);
        Assert.Null(handler.LastStatus); // No request was made
    }

    [Fact]
    public async Task DryRunWithNoProjectIdStillRendersContent()
    {
        var service = CreateService();
        var request = CreateTestRequest(dedupeKey: "dedupe-dry-run-noproj-1") with
        {
            DryRun = true,
            SourceProjectId = null
        };

        var result = await service.NotifyAsync(request);

        Assert.Equal("dry_run", result.Status);
        Assert.NotNull(result.DryRunPayload);
        Assert.Contains("Notification from test-runner", result.DryRunPayload.Content);
        // Should not reference project
        Assert.DoesNotContain("project:", result.DryRunPayload.Content);
    }

    [Fact]
    public async Task WakeByMentionTrueIncludesTargetUserMentionOnly()
    {
        var service = CreateService();
        var request = CreateTestRequest(target: "agent-test-1", dedupeKey: "dedupe-mention-true-1") with { DryRun = true };

        var result = await service.NotifyAsync(request);
        Assert.Equal("dry_run", result.Status);

        var mentions = result.DryRunPayload!.AllowedMentions;
        Assert.Empty(mentions.Parse);
        Assert.Single(mentions.Users);
        Assert.Equal("222222222222222222", mentions.Users[0]);
        Assert.Empty(mentions.Roles);

        // Content should contain the actual Discord mention token
        Assert.Contains("<@222222222222222222>", result.DryRunPayload.Content);
    }

    [Fact]
    public async Task WakeByMentionFalseSuppressesAllMentions()
    {
        var service = CreateService();
        var request = CreateTestRequest(target: "agent-no-mention", dedupeKey: "dedupe-mention-false-1") with { DryRun = true };

        var result = await service.NotifyAsync(request);
        Assert.Equal("dry_run", result.Status);

        var mentions = result.DryRunPayload!.AllowedMentions;
        Assert.Empty(mentions.Parse);
        Assert.Empty(mentions.Users);
        Assert.Empty(mentions.Roles);

        // Content should NOT contain any mention token when WakeByMention=false
        Assert.DoesNotContain("<@", result.DryRunPayload.Content);
    }

    [Fact]
    public async Task DiscordSuccessRecordsIds()
    {
        // Given a successful Discord API response
        var handler = new StubHttpMessageHandler("sent", "discord-message-id-12345");
        var service = CreateService(stubHandler: handler);
        var request = CreateTestRequest(dedupeKey: "dedupe-success-ids-1");

        // When we send
        var result = await service.NotifyAsync(request);

        // Then ids are recorded
        Assert.Equal("sent", result.Status);
        Assert.NotNull(result.NotificationId);
        Assert.NotNull(result.AttemptId);
        Assert.True(result.NotificationId > 0);
        Assert.True(result.AttemptId > 0);
        Assert.Equal("sent", handler.LastStatus);
    }

    [Fact]
    public async Task DiscordErrorRecordsFailedAttemptWithoutTokenLeak()
    {
        // Given a 429 rate limit response
        var handler = new StubHttpMessageHandler("rate_limited", errorCode: "RATE_LIMITED");
        var service = CreateService(stubHandler: handler);
        var request = CreateTestRequest(dedupeKey: "dedupe-rate-limit-1");

        // When we notify
        var result = await service.NotifyAsync(request);

        // Then it returns rate_limited status
        Assert.Equal("rate_limited", result.Status);
        Assert.NotNull(result.NotificationId);
        Assert.NotNull(result.AttemptId);
        Assert.Contains("rate limit", result.Error, StringComparison.OrdinalIgnoreCase);
        // No token should appear in error messages
        Assert.DoesNotContain("test-bot-token", result.Error);
    }

    [Fact]
    public async Task DiscordServerErrorRecordsFailedAttempt()
    {
        var handler = new StubHttpMessageHandler("failed", errorCode: "HTTP_500");
        var service = CreateService(stubHandler: handler);
        var request = CreateTestRequest(dedupeKey: "dedupe-server-error-1");

        var result = await service.NotifyAsync(request);

        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.NotificationId);
        Assert.NotNull(result.AttemptId);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task BodyBoundedMaxLengthIsTruncated()
    {
        var service = CreateService();
        var longBody = new string('A', 3000);
        var request = CreateTestRequest(dedupeKey: "dedupe-truncate-1") with
        {
            Body = longBody,
            DryRun = true
        };

        var result = await service.NotifyAsync(request);
        Assert.Equal("dry_run", result.Status);

        // The dry run content should be truncated to <= MaxBodyLength + some header/footer
        // The content includes header + body + source line, but the body itself should be bounded
        var payload = result.DryRunPayload!;
        Assert.True(payload.Content.Length <= 2500, $"Content length {payload.Content.Length} exceeds expected bound");
        Assert.Contains("...", payload.Content);
    }

    [Fact]
    public async Task EmptyBodyRejectedByValidation()
    {
        var request = CreateTestRequest(dedupeKey: "dedupe-empty-1") with
        {
            Body = ""
        };

        var valid = DiscordNotificationService.ValidateRequest(request, out var error);
        Assert.False(valid);
        Assert.Contains("body", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingFieldsRejectedByValidation()
    {
        // Missing target
        var request1 = new DiscordNotificationRequest(
            TargetAgentIdentity: "",
            Body: "test",
            SourceChannelId: "ch",
            SourceMessageId: "msg",
            SourceProjectId: null,
            Requester: "runner",
            Urgency: null,
            DedupeKey: "dk",
            DryRun: null);
        Assert.False(DiscordNotificationService.ValidateRequest(request1, out var err1));
        Assert.Contains("target_agent_identity", err1, StringComparison.OrdinalIgnoreCase);

        // Missing source_channel_id
        var request2 = request1 with
        {
            TargetAgentIdentity = "agent-test-1",
            SourceChannelId = ""
        };
        Assert.False(DiscordNotificationService.ValidateRequest(request2, out var err2));
        Assert.Contains("source_channel_id", err2, StringComparison.OrdinalIgnoreCase);

        // Missing requester
        var request3 = request2 with
        {
            SourceChannelId = "ch",
            Requester = ""
        };
        Assert.False(DiscordNotificationService.ValidateRequest(request3, out var err3));
        Assert.Contains("requester", err3, StringComparison.OrdinalIgnoreCase);

        // Missing dedupe_key
        var request4 = request3 with
        {
            Requester = "runner",
            DedupeKey = ""
        };
        Assert.False(DiscordNotificationService.ValidateRequest(request4, out var err4));
        Assert.Contains("dedupe_key", err4, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CooldownPreventsRepeatedSendsToSameTarget()
    {
        // Given a target with a short cooldown
        var options = new DiscordBridgeOptions
        {
            Enabled = true,
            BotToken = "test-bot-token-12345",
            CooldownSeconds = 60,
            MaxBodyLength = 2000,
            Targets = new Dictionary<string, DiscordBridgeTarget>
            {
                ["agent-test-1"] = new()
                {
                    ChannelId = "111111111111111111",
                    ThreadId = null,
                    MentionUserId = "222222222222222222",
                    WakeByMention = true
                }
            }
        };
        var handler = new StubHttpMessageHandler("sent", "first-message");
        var service = CreateService(options: options, stubHandler: handler);

        var request1 = CreateTestRequest(target: "agent-test-1", dedupeKey: $"dedupe-cool-1-{Guid.NewGuid():N}");
        var request2 = CreateTestRequest(target: "agent-test-1", dedupeKey: $"dedupe-cool-2-{Guid.NewGuid():N}");

        // First request succeeds
        var firstResult = await service.NotifyAsync(request1);
        Assert.Equal("sent", firstResult.Status);
        Assert.Equal("sent", handler.LastStatus);

        // Second request to same target should be cooldown (within 60s)
        handler.Reset();
        var secondResult = await service.NotifyAsync(request2);
        Assert.Equal("cooldown", secondResult.Status);
        Assert.Null(handler.LastStatus); // No Discord call made
        Assert.Contains("cooldown", secondResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== HELPERS ====================

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "den-gateway-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "discord-bridge.db");
    }
}

/// <summary>Stub HTTP message handler that simulates Discord API responses.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly string _status;
    private readonly string? _messageId;
    private readonly string? _errorCode;

    public string? LastStatus { get; private set; }

    public StubHttpMessageHandler(string status, string? messageId = null, string? errorCode = null)
    {
        _status = status;
        _messageId = messageId;
        _errorCode = errorCode;
    }

    public void Reset() => LastStatus = null;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastStatus = _status;

        HttpResponseMessage response;
        if (_status == "sent")
        {
            var json = JsonSerializer.Serialize(new { id = _messageId ?? "stub-message-id" });
            response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
        else if (_status == "rate_limited")
        {
            var json = JsonSerializer.Serialize(new { message = "You are being rate limited.", retry_after = 2.0, global = false });
            response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
        else
        {
            var json = JsonSerializer.Serialize(new { code = 50001, message = "Missing Access" });
            response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        return Task.FromResult(response);
    }
}

/// <summary>Stub IHttpClientFactory that returns a pre-configured HttpClient.</summary>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public StubHttpClientFactory(HttpClient client)
    {
        _client = client;
    }

    public HttpClient CreateClient(string name) => _client;
}
