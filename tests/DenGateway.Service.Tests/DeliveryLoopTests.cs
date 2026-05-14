using System.Net.Http.Json;
using DenGateway.Service.Clients;
using DenGateway.Service.DeliveryLoop;
using DenGateway.Service.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenGateway.Service.Tests;

public class DeliveryLoopTests
{
    [Fact]
    public async Task CoreOutboxEventIsPersistedAsOneGatewayDeliveryWithSourceSummaryMetadata()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var core = new FakeDenCoreClient
        {
            OutboxEvents =
            [
                new GatewayOutboxEvent(
                    Cursor: "42",
                    EventId: "core-event-42",
                    EventType: "agent_stream_message",
                    ProjectId: "den-gateway",
                    SourceKind: "agent_stream_message",
                    SourceId: "5850",
                    OccurredAt: DateTimeOffset.Parse("2026-05-14T03:00:00Z"),
                    Actor: "user",
                    SummaryHint: "Please wake runner",
                    DeepLink: "den://project/den-gateway/message/5850",
                    Severity: "normal",
                    DedupeKey: "core:agent-stream:5850" )
            ],
            SourceSummary = new SourceSummary(
                SourceKind: "agent_stream_message",
                SourceId: "5850",
                SourceProjectId: "den-gateway",
                Title: "Agent stream mention",
                Summary: "Please wake runner",
                DeepLink: "den://project/den-gateway/message/5850",
                OccurredAt: DateTimeOffset.Parse("2026-05-14T03:00:00Z"),
                Actor: "user",
                Severity: "normal",
                Metadata: new Dictionary<string, string>
                {
                    ["targetType"] = "agent",
                    ["targetIdentity"] = "den-gateway-runner",
                    ["deliveryMode"] = "wake",
                    ["taskId"] = "1406",
                    ["reason"] = "explicit_mention"
                })
        };
        var service = new GatewayDeliveryLoopService(database, core, new FakeDenChannelsClient());

        var first = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "core", ProjectId: "den-gateway", Limit: 10, Now: DateTimeOffset.Parse("2026-05-14T03:01:00Z")));
        var second = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "core", ProjectId: "den-gateway", Limit: 10, Now: DateTimeOffset.Parse("2026-05-14T03:02:00Z")));

        Assert.Equal("completed", first.Status);
        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(1, first.SeenCount);
        Assert.Equal("42", first.NextCursor);
        Assert.Equal("completed", second.Status);
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(1, second.DuplicateCount);

        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("agent_stream_message", row.SourceKind);
        Assert.Equal("5850", row.SourceId);
        Assert.Equal("agent", row.TargetType);
        Assert.Equal("den-gateway-runner", row.TargetIdentity);
        Assert.Equal("wake", row.DeliveryMode);
        Assert.Equal("pending", row.Status);
        Assert.Equal("core:agent-stream:5850", row.DedupeKey);
        Assert.Contains("core-event-42", row.MetadataJson);
        Assert.Equal(1406, row.TaskId);
    }

    [Fact]
    public async Task ChannelEventCreatesOneWakePerActiveWakeMembershipAndSkipsSender()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot(
                    Cursor: "100",
                    EventType: "message_created",
                    ChannelId: "77",
                    SourceKind: "channel_message",
                    SourceId: "100",
                    DedupeKey: "channel-message:100",
                    OccurredAt: DateTimeOffset.Parse("2026-05-14T03:05:00Z"))
            ],
            Message = new ChannelMessageSnapshot(
                ChannelMessageId: "100",
                ChannelId: "77",
                SenderType: "agent",
                SenderIdentity: "den-gateway-planner",
                MessageKind: "message_created",
                Body: "runner please take this",
                SourceKind: "channel_message",
                SourceId: "100",
                DedupeKey: "channel-message:100",
                CreatedAt: DateTimeOffset.Parse("2026-05-14T03:05:00Z")),
            Memberships =
            [
                new ChannelMembershipSnapshot("77", "agent", "den-gateway-runner", "wake", "active", 0, new Dictionary<string, string> { ["projectId"] = "den-gateway" }),
                new ChannelMembershipSnapshot("77", "agent", "den-gateway-planner", "wake", "active", 0, new Dictionary<string, string> { ["projectId"] = "den-gateway" }),
                new ChannelMembershipSnapshot("77", "agent", "quiet-agent", "record_only", "active", 0, new Dictionary<string, string> { ["projectId"] = "den-gateway" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "den-gateway", Limit: 10, Now: DateTimeOffset.Parse("2026-05-14T03:06:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SuppressedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("channel_message", row.SourceKind);
        Assert.Equal("100", row.SourceId);
        Assert.Equal("agent", row.TargetType);
        Assert.Equal("den-gateway-runner", row.TargetIdentity);
        Assert.Equal("wake", row.DeliveryMode);
        Assert.Equal("channel-message:100:agent:den-gateway-runner", row.DedupeKey);
        Assert.Equal("77", row.ChannelId);
        Assert.Contains("runner please take this", row.ContextSummary);
    }

    [Fact]
    public async Task UnavailableUpstreamDoesNotCreateDeliveryAndReturnsDegraded()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var core = new FakeDenCoreClient { OutboxAvailable = false };
        var service = new GatewayDeliveryLoopService(database, core, new FakeDenChannelsClient());

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "core", ProjectId: "den-gateway", Limit: 10, Now: DateTimeOffset.Parse("2026-05-14T03:10:00Z")));

        Assert.Equal("degraded", result.Status);
        Assert.Equal("core_unavailable", result.ErrorCode);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
    }

    [Fact]
    public async Task CoreOutboxEventMissingSourcePointerIsSuppressedInsteadOfThrowing()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var core = new FakeDenCoreClient
        {
            OutboxEvents =
            [
                new GatewayOutboxEvent("null-source", "core-event-null", "unknown", "den-gateway", null!, null!, DateTimeOffset.Parse("2026-05-14T04:10:00Z"), "system", "bad event", null, "normal", "core:null-source")
            ]
        };
        var service = new GatewayDeliveryLoopService(database, core, new FakeDenChannelsClient());

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest("core", "den-gateway", 10, DateTimeOffset.Parse("2026-05-14T04:11:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(1, result.SuppressedCount);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
    }

    [Fact]
    public async Task PollEndpointRunsDeliveryLoopAndReturnsCounts()
    {
        var databasePath = CreateTempDatabasePath();
        var core = new FakeDenCoreClient
        {
            OutboxEvents =
            [
                new GatewayOutboxEvent("9", "core-event-9", "agent_stream_message", "den-gateway", "agent_stream_message", "5900", DateTimeOffset.Parse("2026-05-14T04:00:00Z"), "user", "wake runner", null, "normal", "core:5900")
            ],
            SourceSummary = new SourceSummary("agent_stream_message", "5900", "den-gateway", "Mention", "wake runner", "den://project/den-gateway/message/5900", DateTimeOffset.Parse("2026-05-14T04:00:00Z"), "user", "normal", new Dictionary<string, string>
            {
                ["targetType"] = "agent",
                ["targetIdentity"] = "den-gateway-runner",
                ["deliveryMode"] = "wake"
            })
        };
        await using var factory = new WebApplicationFactory<Program>()
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
                    services.AddSingleton<IDenCoreClient>(core);
                    services.AddSingleton<IDenChannelsClient>(new FakeDenChannelsClient());
                });
            });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/delivery-loop/poll", new { source = "core", project_id = "den-gateway", limit = 5, now = "2026-05-14T04:01:00Z" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<GatewayDeliveryPollResult>();
        Assert.NotNull(body);
        Assert.Equal("completed", body.Status);
        Assert.Equal(1, body.CreatedCount);
        Assert.Equal(1, await CountDeliveriesAsync(databasePath));
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "den-gateway-tests", Guid.NewGuid().ToString("N"));
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

    private static async Task<DeliveryRow> ReadSingleDeliveryAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_kind, source_id, target_type, target_identity, delivery_mode, status,
                   dedupe_key, metadata_json, task_id, channel_id, context_summary
            FROM delivery_requests
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = new DeliveryRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private sealed record DeliveryRow(string SourceKind, string? SourceId, string TargetType, string TargetIdentity, string DeliveryMode, string Status, string DedupeKey, string MetadataJson, int? TaskId, string? ChannelId, string? ContextSummary);

    private sealed class FakeDenCoreClient : IDenCoreClient
    {
        public bool OutboxAvailable { get; init; } = true;
        public IReadOnlyList<GatewayOutboxEvent> OutboxEvents { get; init; } = [];
        public SourceSummary? SourceSummary { get; init; }
        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));
        public Task<ClientListResult<GatewayBindingSnapshot>> ListActiveBindingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<GatewayBindingSnapshot>.Available([]));
        public Task<ClientValueResult<SourceSummary>> GetSourceSummaryAsync(string sourceKind, string sourceId, string? projectId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sourceKind);
            ArgumentNullException.ThrowIfNull(sourceId);
            return Task.FromResult(SourceSummary is null ? ClientValueResult<SourceSummary>.Unavailable("not_found", "missing") : ClientValueResult<SourceSummary>.Available(SourceSummary));
        }
        public Task<ClientListResult<GatewayOutboxEvent>> ReadEventOutboxAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default) => Task.FromResult(OutboxAvailable ? ClientListResult<GatewayOutboxEvent>.Available(OutboxEvents.Take(limit).ToArray()) : ClientListResult<GatewayOutboxEvent>.Unavailable("offline", "core offline"));
        public Task<ClientOperationResult> PostGatewayReconciliationEventsAsync(IReadOnlyList<GatewayReconciliationEvent> events, CancellationToken cancellationToken = default) => Task.FromResult(ClientOperationResult.Completed("ok"));
    }

    private sealed class FakeDenChannelsClient : IDenChannelsClient
    {
        public bool EventsAvailable { get; init; } = true;
        public IReadOnlyList<ChannelEventSnapshot> Events { get; init; } = [];
        public IReadOnlyList<ChannelMembershipSnapshot> Memberships { get; init; } = [];
        public ChannelMessageSnapshot? Message { get; init; }
        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));
        public Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default) => Task.FromResult(Message is null ? ClientValueResult<ChannelMessageSnapshot>.Unavailable("not_found", "missing") : ClientValueResult<ChannelMessageSnapshot>.Available(Message));
        public Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<ChannelMembershipSnapshot>.Available(Memberships.Where(m => m.ChannelId == channelId).ToArray()));
        public Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default) => Task.FromResult(ClientOperationResult.Completed("ok"));
        public Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default) => Task.FromResult(EventsAvailable ? ClientListResult<ChannelEventSnapshot>.Available(Events.Take(limit).ToArray()) : ClientListResult<ChannelEventSnapshot>.Unavailable("offline", "channels offline"));
    }
}
