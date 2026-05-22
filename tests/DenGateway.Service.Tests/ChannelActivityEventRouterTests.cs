using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DenGateway.Service.Clients;
using DenGateway.Service.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenGateway.Service.Tests;

public sealed class ChannelActivityEventRouterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ActivityRoutePreservesHermesPluginDisplayBlockPayloadsWithoutCreatingDeliveryRequestsOrWakes()
    {
        var databasePath = CreateTempDatabasePath();
        var channels = new RecordingChannelsClient();
        await using var factory = NewFactory(databasePath, channels);
        using var client = factory.CreateClient();

        var coderPayload = new
        {
            channelId = "42",
            projectId = "den-channels",
            agentIdentity = "den-coder-profile",
            deliveryRequestId = "coder-1567",
            displayBlockId = "parent-1567",
            hermesSessionKey = "session-coder-1567",
            parentHermesSessionKey = "session-parent-1567",
            parentAgentIdentity = "den-mcp-runner",
            workerRunId = "coder-1567",
            workerRole = "coder",
            taskId = 1567,
            threadId = 7001,
            anchorMessageId = 9001,
            eventType = "tool_call_started",
            status = "started",
            sequence = 1,
            title = "terminal",
            summary = "dotnet test",
            previewJson = "{\"command\":\"dotnet test\"}",
            metadataJson = "{\"phase\":\"coder\"}",
            dedupeKey = "activity:coder-1567:1"
        };
        var reviewerPayload = new
        {
            channelId = "42",
            projectId = "den-channels",
            agentIdentity = "den-reviewer-profile",
            deliveryRequestId = "reviewer-1567",
            displayBlockId = "parent-1567",
            hermesSessionKey = "session-reviewer-1567",
            parentHermesSessionKey = "session-parent-1567",
            parentAgentIdentity = "den-mcp-runner",
            workerRunId = "reviewer-1567",
            workerRole = "reviewer",
            taskId = 1567,
            threadId = 7001,
            anchorMessageId = 9001,
            eventType = "review_started",
            status = "started",
            sequence = 2,
            title = "review",
            summary = "fake E2E reviewer activity",
            previewJson = "{\"round\":1}",
            metadataJson = "{\"phase\":\"reviewer\"}",
            dedupeKey = "activity:reviewer-1567:2"
        };

        using var coderResponse = await client.PostAsJsonAsync("/api/channel-activity-events", coderPayload);
        using var reviewerResponse = await client.PostAsJsonAsync("/api/channel-activity-events", reviewerPayload);

        await AssertRecordedAsync(coderResponse);
        await AssertRecordedAsync(reviewerResponse);
        Assert.Collection(channels.ActivityEvents,
            write => AssertActivityWriteMatchesGatewayPayload(coderPayload, write),
            write => AssertActivityWriteMatchesGatewayPayload(reviewerPayload, write));
        Assert.All(channels.ActivityEvents, write =>
        {
            Assert.Equal("parent-1567", write.DisplayBlockId);
            Assert.NotEqual(write.DeliveryRequestId, write.DisplayBlockId);
        });
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
        Assert.Equal(0, await CountTableRowsAsync(databasePath, "delivery_attempts"));
        Assert.Equal(0, await CountTableRowsAsync(databasePath, "sentinel_events"));
    }

    [Fact]
    public async Task ActivityRouteAcceptsMinimalPayloadWithoutDisplayBlockOrWorkerMetadata()
    {
        var databasePath = CreateTempDatabasePath();
        var channels = new RecordingChannelsClient();
        await using var factory = NewFactory(databasePath, channels);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/channel-activity-events", new
        {
            channelId = "42",
            agentIdentity = "den-mcp-runner"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActivityRoutePayload>();
        Assert.NotNull(result);
        Assert.Equal("recorded", result.Status);
        Assert.True(result.Recorded);

        var write = Assert.Single(channels.ActivityEvents);
        Assert.Null(write.DisplayBlockId);
        Assert.Null(write.ParentHermesSessionKey);
        Assert.Null(write.ParentAgentIdentity);
        Assert.Null(write.WorkerRunId);
        Assert.Null(write.WorkerRole);
        Assert.Equal("lifecycle_status", write.EventType);
        Assert.Equal("interim", write.Status);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
    }

    [Fact]
    public async Task ActivityRouteFailureIsSoftAndRecordedInDiagnostics()
    {
        var databasePath = CreateTempDatabasePath();
        var channels = new RecordingChannelsClient { ActivityAvailable = false };
        await using var factory = NewFactory(databasePath, channels);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/channel-activity-events", new
        {
            channelId = "42",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-1527",
            displayBlockId = "display-block-1564",
            workerRunId = "worker-run-1564",
            eventType = "tool_call_failed"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActivityRoutePayload>();
        Assert.NotNull(result);
        Assert.Equal("degraded", result.Status);
        Assert.False(result.Recorded);
        Assert.Equal("offline", result.ErrorCode);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));

        var status = await client.GetFromJsonAsync<ActivityRouterStatusPayload>("/api/channel-activity-events/status");
        Assert.NotNull(status);
        var failure = Assert.Single(status.RecentFailures);
        Assert.Equal("42", failure.ChannelId);
        Assert.Equal("delivery-1527", failure.DeliveryRequestId);
        Assert.Equal("display-block-1564", failure.DisplayBlockId);
        Assert.Equal("worker-run-1564", failure.WorkerRunId);
        Assert.Equal("offline", failure.ErrorCode);
    }

    [Fact]
    public async Task ActivityRouteExceptionIsSoftAndRecordedInDiagnostics()
    {
        var databasePath = CreateTempDatabasePath();
        var channels = new RecordingChannelsClient { ThrowOnActivity = true };
        await using var factory = NewFactory(databasePath, channels);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/channel-activity-events", new
        {
            channelId = "42",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-exception-1527",
            eventType = "tool_call_failed"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActivityRoutePayload>();
        Assert.NotNull(result);
        Assert.Equal("degraded", result.Status);
        Assert.False(result.Recorded);
        Assert.Equal("activity_record_exception", result.ErrorCode);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));

        var status = await client.GetFromJsonAsync<ActivityRouterStatusPayload>("/api/channel-activity-events/status");
        Assert.NotNull(status);
        var failure = Assert.Single(status.RecentFailures);
        Assert.Equal("delivery-exception-1527", failure.DeliveryRequestId);
        Assert.Equal("activity_record_exception", failure.ErrorCode);
    }

    private static WebApplicationFactory<Program> NewFactory(string databasePath, IDenChannelsClient channels)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DenGateway:Database:Path"] = databasePath,
                        ["DenGateway:DenCore:UseStub"] = "true",
                        ["DenGateway:DenChannels:UseStub"] = "true"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IDenChannelsClient>(channels);
                });
            });
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "den-gateway-activity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "den-gateway.db");
    }

    private static async Task<int> CountDeliveriesAsync(string databasePath)
    {
        return await CountTableRowsAsync(databasePath, "delivery_requests");
    }

    private static async Task<int> CountTableRowsAsync(string databasePath, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertRecordedAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActivityRoutePayload>();
        Assert.NotNull(result);
        Assert.Equal("recorded", result.Status);
        Assert.True(result.Recorded);
        Assert.Equal("activity-99", result.ActivityEventId);
    }

    private static void AssertActivityWriteMatchesGatewayPayload<T>(T expectedPayload, ChannelActivityEventWrite actual)
    {
        var expected = JsonSerializer.SerializeToNode(expectedPayload, JsonOptions)?.AsObject();
        var serializedActual = JsonSerializer.SerializeToNode(actual, JsonOptions)?.AsObject();
        Assert.NotNull(expected);
        Assert.NotNull(serializedActual);
        Assert.False(expected.ContainsKey("displayDeliveryRequestId"));
        Assert.False(serializedActual.ContainsKey("displayDeliveryRequestId"));
        Assert.Equal(expected.Select(property => property.Key).Order(StringComparer.Ordinal),
            serializedActual.Select(property => property.Key).Order(StringComparer.Ordinal));
        foreach (var (key, expectedValue) in expected)
        {
            Assert.True(serializedActual.TryGetPropertyValue(key, out var actualValue), $"Missing activity payload field {key}.");
            Assert.Equal(expectedValue?.ToJsonString(), actualValue?.ToJsonString());
        }
    }

    private sealed class RecordingChannelsClient : IDenChannelsClient
    {
        public bool ActivityAvailable { get; init; } = true;
        public bool ThrowOnActivity { get; init; }
        public List<ChannelActivityEventWrite> ActivityEvents { get; } = [];

        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ServiceHealthResult.Available("fake", "ok"));

        public Task<ClientValueResult<ChannelMembershipListSnapshot>> ListProjectMembershipsAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientValueResult<ChannelMembershipListSnapshot>.Unavailable("not_found", "missing"));

        public Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientValueResult<ChannelMessageSnapshot>.Unavailable("not_found", "missing"));

        public Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientListResult<ChannelMembershipSnapshot>.Available([]));

        public Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientOperationResult.Completed("ok"));

        public Task<ChannelActivityPostResult> PostActivityEventAsync(ChannelActivityEventWrite activityEvent, CancellationToken cancellationToken = default)
        {
            if (ThrowOnActivity)
            {
                throw new HttpRequestException("channels activity endpoint unavailable");
            }

            ActivityEvents.Add(activityEvent);
            return Task.FromResult(ActivityAvailable
                ? ChannelActivityPostResult.Completed("activity-99", "ok")
                : ChannelActivityPostResult.Unavailable("offline", "channels offline"));
        }

        public Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, string? channelId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientListResult<ChannelEventSnapshot>.Available([]));

        public Task<ClientValueResult<string>> GetLatestChannelEventCursorAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientValueResult<string>.Unavailable("empty_cursor", "no events"));
    }

    private sealed record ActivityRoutePayload(string Status, bool Recorded, string? ActivityEventId, string? ErrorCode, string? Message);
    private sealed record ActivityRouterStatusPayload(IReadOnlyList<ActivityDiagnosticPayload> RecentFailures);
    private sealed record ActivityDiagnosticPayload(string ChannelId, string? DeliveryRequestId, string? DisplayBlockId, string? WorkerRunId, string ErrorCode);
}
