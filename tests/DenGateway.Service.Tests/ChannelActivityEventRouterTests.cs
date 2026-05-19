using System.Net;
using System.Net.Http.Json;
using DenGateway.Service.Clients;
using DenGateway.Service.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenGateway.Service.Tests;

public sealed class ChannelActivityEventRouterTests
{
    [Fact]
    public async Task ActivityRoutePostsToChannelsWithoutCreatingDeliveryRequests()
    {
        var databasePath = CreateTempDatabasePath();
        var channels = new RecordingChannelsClient();
        await using var factory = NewFactory(databasePath, channels);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/channel-activity-events", new
        {
            channelId = "42",
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-1527",
            hermesSessionKey = "session-1527",
            taskId = 1527,
            threadId = 6448,
            anchorMessageId = 101,
            eventType = "tool_call_started",
            status = "started",
            sequence = 1,
            title = "terminal",
            summary = "dotnet test",
            dedupeKey = "activity:delivery-1527:1"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActivityRoutePayload>();
        Assert.NotNull(result);
        Assert.Equal("recorded", result.Status);
        Assert.True(result.Recorded);
        Assert.Equal("activity-99", result.ActivityEventId);
        var write = Assert.Single(channels.ActivityEvents);
        Assert.Equal("42", write.ChannelId);
        Assert.Equal("delivery-1527", write.DeliveryRequestId);
        Assert.Equal("session-1527", write.HermesSessionKey);
        Assert.Equal("tool_call_started", write.EventType);
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
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM delivery_requests;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
    private sealed record ActivityDiagnosticPayload(string ChannelId, string? DeliveryRequestId, string ErrorCode);
}
