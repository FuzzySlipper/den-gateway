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
    public async Task ChannelHumanMessageWithAllHumanMessagesPolicyCreatesWakeDelivery()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot(
                    Cursor: "101",
                    EventType: "message_created",
                    ChannelId: "77",
                    SourceKind: "channel_message",
                    SourceId: "101",
                    DedupeKey: "channel-message:101",
                    OccurredAt: DateTimeOffset.Parse("2026-05-16T23:06:23Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["101"] = new ChannelMessageSnapshot(
                    ChannelMessageId: "101",
                    ChannelId: "77",
                    SenderType: "user",
                    SenderIdentity: "Patch",
                    MessageKind: "human_text",
                    Body: "Testing a message",
                    SourceKind: "wake_event",
                    SourceId: "direct-agent-message:77:1:1778972783265",
                    DedupeKey: "channel-message:101",
                    CreatedAt: DateTimeOffset.Parse("2026-05-16T23:06:23Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("77", "agent", "den-channels-runner", "all_human_messages", "active", 60, new Dictionary<string, string>
                {
                    ["projectId"] = "den-channels"
                })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "den-channels", Limit: 10, Now: DateTimeOffset.Parse("2026-05-16T23:07:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("channel_message", row.SourceKind);
        Assert.Equal("101", row.SourceId);
        Assert.Equal("agent", row.TargetType);
        Assert.Equal("den-channels-runner", row.TargetIdentity);
        Assert.Equal("wake", row.DeliveryMode);
        Assert.Equal("den-channels", row.ProjectId);
        Assert.Contains("all_human_messages", row.MetadataJson);
    }

    [Fact]
    public async Task ChannelMentionsOnlyPolicyRequiresExplicitMention()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("201", "message_created", "77", "channel_message", "201", "channel-message:201", DateTimeOffset.Parse("2026-05-16T23:10:00Z")),
                new ChannelEventSnapshot("202", "message_created", "77", "channel_message", "202", "channel-message:202", DateTimeOffset.Parse("2026-05-16T23:11:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["201"] = new ChannelMessageSnapshot("201", "77", "user", "Patch", "human_text", "hello team", "channel_message", "201", "channel-message:201", DateTimeOffset.Parse("2026-05-16T23:10:00Z")),
                ["202"] = new ChannelMessageSnapshot("202", "77", "user", "Patch", "human_text", "@den-channels-runner please respond", "channel_message", "202", "channel-message:202", DateTimeOffset.Parse("2026-05-16T23:11:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("77", "agent", "den-channels-runner", "mentions_only", "active", 60, new Dictionary<string, string> { ["projectId"] = "den-channels" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "den-channels", Limit: 10, Now: DateTimeOffset.Parse("2026-05-16T23:12:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SuppressedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("202", row.SourceId);
        Assert.Equal("wake", row.DeliveryMode);
    }

    [Fact]
    public async Task AgentCommonsDirectAgentWakeTargetsOnlyEncodedMemberIdentity()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("372", "message_created", "21", "wake_event", "direct-agent-message:21:reviewer:1779267407642", "channel-message:372", DateTimeOffset.Parse("2026-05-20T09:00:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["372"] = new ChannelMessageSnapshot("372", "21", "user", "den-web", "human_text", "Please review task 1557", "wake_event", "direct-agent-message:21:reviewer:1779267407642", "channel-message:372", DateTimeOffset.Parse("2026-05-20T09:00:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("21", "agent", "reviewer", "mentions_only", "active", 60, new Dictionary<string, string>()),
                new ChannelMembershipSnapshot("21", "agent", "den-mcp-runner", "mentions_only", "active", 60, new Dictionary<string, string>()),
                new ChannelMembershipSnapshot("21", "agent", "sysadmin", "mentions_only", "active", 60, new Dictionary<string, string>())
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: null, Limit: 10, Now: DateTimeOffset.Parse("2026-05-20T09:01:00Z"), ChannelId: "21"));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(2, result.SuppressedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("reviewer", row.TargetIdentity);
        Assert.Equal("wake", row.DeliveryMode);
        Assert.Contains("direct_agent_target", row.MetadataJson);
    }

    [Fact]
    public async Task DirectAgentEncodedTargetCreatesWakeWhenMessageSourceKindDiffersFromEvent()
    {
        // Real-world scenario: the channel event correctly carries
        // SourceKind="wake_event" and the direct-agent-message sourceId,
        // but the channel message content has SourceKind="channel_message"
        // (because the human text message was originally posted as a regular
        // channel message). The Gateway must prefer the event's routing
        // metadata to resolve the encoded target identity. Without this fix,
        // TryGetDirectAgentTargetIdentity would use the message's
        // "channel_message" SourceKind, return null, and the mentions_only
        // policy would suppress the delivery since the body lacks @mention.
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("805", "message_created", "4", "wake_event", "direct-agent-message:4:den-mcp-runner:1779610424738", "channel-message:805", DateTimeOffset.Parse("2026-05-20T10:00:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["805"] = new ChannelMessageSnapshot("805", "4", "user", "Patch", "human_text", "Please handle this task", "channel_message", "805", "channel-message:805", DateTimeOffset.Parse("2026-05-20T10:00:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("4", "agent", "den-mcp-runner", "mentions_only", "active", 60, new Dictionary<string, string>()),
                new ChannelMembershipSnapshot("4", "agent", "reviewer", "mentions_only", "active", 60, new Dictionary<string, string>()),
                new ChannelMembershipSnapshot("4", "agent", "sysadmin", "mentions_only", "active", 60, new Dictionary<string, string>())
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: null, Limit: 10, Now: DateTimeOffset.Parse("2026-05-20T10:01:00Z"), ChannelId: "4"));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(2, result.SuppressedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("den-mcp-runner", row.TargetIdentity);
        Assert.Equal("wake", row.DeliveryMode);
        Assert.Contains("direct_agent_target", row.MetadataJson);
    }

    [Fact]
    public async Task AmbiguousLegacyDirectAgentWakeEventDoesNotBypassMentionsOnly()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("373", "message_created", "21", "wake_event", "direct-agent-message:21:44:1779267407642", "channel-message:373", DateTimeOffset.Parse("2026-05-20T09:02:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["373"] = new ChannelMessageSnapshot("373", "21", "user", "den-web", "human_text", "Please review task 1557", "wake_event", "direct-agent-message:21:44:1779267407642", "channel-message:373", DateTimeOffset.Parse("2026-05-20T09:02:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("21", "agent", "reviewer", "mentions_only", "active", 60, new Dictionary<string, string>()),
                new ChannelMembershipSnapshot("21", "agent", "den-mcp-runner", "mentions_only", "active", 60, new Dictionary<string, string>())
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: null, Limit: 10, Now: DateTimeOffset.Parse("2026-05-20T09:03:00Z"), ChannelId: "21"));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(2, result.SuppressedCount);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
    }

    [Fact]
    public async Task ChannelAllMessagesExceptSelfSuppressesMatchingSenderIdentity()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("301", "message_created", "77", "channel_message", "301", "channel-message:301", DateTimeOffset.Parse("2026-05-16T23:13:00Z")),
                new ChannelEventSnapshot("302", "message_created", "77", "channel_message", "302", "channel-message:302", DateTimeOffset.Parse("2026-05-16T23:14:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["301"] = new ChannelMessageSnapshot("301", "77", "user", "den-channels-runner", "human_text", "self-authored forwarded message", "channel_message", "301", "channel-message:301", DateTimeOffset.Parse("2026-05-16T23:13:00Z")),
                ["302"] = new ChannelMessageSnapshot("302", "77", "user", "Patch", "human_text", "external human message", "channel_message", "302", "channel-message:302", DateTimeOffset.Parse("2026-05-16T23:14:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("77", "agent", "den-channels-runner", "all_messages_except_self", "active", 60, new Dictionary<string, string> { ["projectId"] = "den-channels" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "den-channels", Limit: 10, Now: DateTimeOffset.Parse("2026-05-16T23:15:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SuppressedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("302", row.SourceId);
        Assert.Equal("wake", row.DeliveryMode);
    }

    [Fact]
    public async Task ChannelAllMessagesExceptSelfWakesBothAgentsOnceForHumanMessage()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("152", "message_created", "12", "channel_message", "152", "channel-message:152", DateTimeOffset.Parse("2026-05-18T09:00:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["152"] = new ChannelMessageSnapshot("152", "12", "user", "Patch", "human_text", "please both take a look", "channel_message", "152", "channel-message:152", DateTimeOffset.Parse("2026-05-18T09:00:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("12", "agent", "quillforge-planner", "all_messages_except_self", "active", 60, new Dictionary<string, string> { ["projectId"] = "quillforge" }),
                new ChannelMembershipSnapshot("12", "agent", "quillforge-runner", "all_messages_except_self", "active", 60, new Dictionary<string, string> { ["projectId"] = "quillforge" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "quillforge", Limit: 10, Now: DateTimeOffset.Parse("2026-05-18T09:01:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(0, result.SuppressedCount);
        var rows = await ReadDeliveriesAsync(databasePath);
        Assert.Equal(["quillforge-planner", "quillforge-runner"], rows.Select(row => row.TargetIdentity).OrderBy(identity => identity).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Equal("wake", row.DeliveryMode);
            Assert.Contains("\"cascade_depth\":0", row.MetadataJson);
        });
    }

    [Fact]
    public async Task ChannelAllMessagesExceptSelfSuppressesPeerAgentGatewayDeliveryReplies()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("154", "message_created", "12", "channel_message", "154", "channel-message:154", DateTimeOffset.Parse("2026-05-18T09:02:00Z")),
                new ChannelEventSnapshot("155", "message_created", "12", "channel_message", "155", "channel-message:155", DateTimeOffset.Parse("2026-05-18T09:02:02Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["154"] = new ChannelMessageSnapshot("154", "12", "agent", "quillforge-runner", "agent_text", "I'll inspect that now", "gateway_delivery", "90", "gateway-delivery:90:interim", DateTimeOffset.Parse("2026-05-18T09:02:00Z")),
                ["155"] = new ChannelMessageSnapshot("155", "12", "agent", "quillforge-runner", "agent_text", "Done, nothing else to add", "gateway_delivery", "90", "gateway-delivery:90:final", DateTimeOffset.Parse("2026-05-18T09:02:02Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("12", "agent", "quillforge-planner", "all_messages_except_self", "active", 60, new Dictionary<string, string> { ["projectId"] = "quillforge" }),
                new ChannelMembershipSnapshot("12", "agent", "quillforge-runner", "all_messages_except_self", "active", 60, new Dictionary<string, string> { ["projectId"] = "quillforge" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "quillforge", Limit: 10, Now: DateTimeOffset.Parse("2026-05-18T09:03:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.SeenCount);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(4, result.SuppressedCount);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
    }

    [Fact]
    public async Task ChannelDirectQuestionsOnlyRequiresMentionAndQuestionMark()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("401", "message_created", "77", "channel_message", "401", "channel-message:401", DateTimeOffset.Parse("2026-05-16T23:16:00Z")),
                new ChannelEventSnapshot("402", "message_created", "77", "channel_message", "402", "channel-message:402", DateTimeOffset.Parse("2026-05-16T23:17:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["401"] = new ChannelMessageSnapshot("401", "77", "user", "Patch", "human_text", "@den-channels-runner please respond", "channel_message", "401", "channel-message:401", DateTimeOffset.Parse("2026-05-16T23:16:00Z")),
                ["402"] = new ChannelMessageSnapshot("402", "77", "user", "Patch", "human_text", "@den-channels-runner can you respond?", "channel_message", "402", "channel-message:402", DateTimeOffset.Parse("2026-05-16T23:17:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("77", "agent", "den-channels-runner", "direct_questions_only", "active", 60, new Dictionary<string, string> { ["projectId"] = "den-channels" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "den-channels", Limit: 10, Now: DateTimeOffset.Parse("2026-05-16T23:18:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SuppressedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("402", row.SourceId);
        Assert.Equal("wake", row.DeliveryMode);
    }

    [Fact]
    public async Task ChannelSubstantiveDigestNotifiesForHumanMessagesOnly()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("501", "message_created", "77", "channel_message", "501", "channel-message:501", DateTimeOffset.Parse("2026-05-16T23:19:00Z")),
                new ChannelEventSnapshot("502", "message_created", "77", "channel_message", "502", "channel-message:502", DateTimeOffset.Parse("2026-05-16T23:20:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["501"] = new ChannelMessageSnapshot("501", "77", "agent", "other-agent", "agent_text", "agent chatter", "channel_message", "501", "channel-message:501", DateTimeOffset.Parse("2026-05-16T23:19:00Z")),
                ["502"] = new ChannelMessageSnapshot("502", "77", "user", "Patch", "human_text", "substantive human update", "channel_message", "502", "channel-message:502", DateTimeOffset.Parse("2026-05-16T23:20:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("77", "agent", "den-channels-runner", "substantive_digest", "active", 60, new Dictionary<string, string> { ["projectId"] = "den-channels" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "den-channels", Limit: 10, Now: DateTimeOffset.Parse("2026-05-16T23:21:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SuppressedCount);
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("502", row.SourceId);
        Assert.Equal("notify", row.DeliveryMode);
    }

    [Fact]
    public async Task ChannelUnknownWakePolicyDefaultsToNoDeliveryWithoutSuppression()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("601", "message_created", "77", "channel_message", "601", "channel-message:601", DateTimeOffset.Parse("2026-05-16T23:22:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["601"] = new ChannelMessageSnapshot("601", "77", "user", "Patch", "human_text", "hello", "channel_message", "601", "channel-message:601", DateTimeOffset.Parse("2026-05-16T23:22:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("77", "agent", "den-channels-runner", "future_policy", "active", 60, new Dictionary<string, string> { ["projectId"] = "den-channels" })
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: "den-channels", Limit: 10, Now: DateTimeOffset.Parse("2026-05-16T23:23:00Z")));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.SuppressedCount);
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
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
    public async Task NewProjectCursorCanSeedAtLatestWithoutReplayingBackfill()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var channels = new FakeDenChannelsClient
        {
            LatestCursor = "999",
            Events =
            [
                new ChannelEventSnapshot("900", "message_created", "77", "channel_message", "900", "channel-message:900", DateTimeOffset.Parse("2026-05-17T09:00:00Z"))
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest("channels", "den-network", 10, DateTimeOffset.Parse("2026-05-17T09:05:00Z"), SeedCursorAtLatestWhenMissing: true));

        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.SeenCount);
        Assert.Equal("999", result.NextCursor);
        Assert.Equal(0, channels.ReadEventsCalls);
        Assert.Equal("999", await database.ReadDeliveryLoopCursorAsync("channels", "den-network"));
        Assert.Equal(0, await CountDeliveriesAsync(databasePath));
    }

    [Fact]
    public async Task ChannelScopedPollUsesChannelIdAndSeparateCursorScopeForGlobalSystemChannels()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        await database.UpsertDeliveryLoopCursorAsync("channels", "den-channels", "555", DateTimeOffset.Parse("2026-05-19T09:00:00Z"));
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("701", "message_created", "21", "channel_message", "701", "channel-message:701", DateTimeOffset.Parse("2026-05-19T09:30:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["701"] = new ChannelMessageSnapshot("701", "21", "user", "Patch", "human_text", "@reviewer please check agent commons", "wake_event", "direct-agent-message:21:701", "channel-message:701", DateTimeOffset.Parse("2026-05-19T09:30:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("21", "agent", "reviewer", "mentions_only", "active", 60, new Dictionary<string, string>())
            ]
        };
        var service = new GatewayDeliveryLoopService(database, new FakeDenCoreClient(), channels);

        var result = await service.PollOnceAsync(new GatewayDeliveryPollRequest(Source: "channels", ProjectId: null, Limit: 10, Now: DateTimeOffset.Parse("2026-05-19T09:31:00Z"), ChannelId: "21"));

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SeenCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Null(channels.LastProjectId);
        Assert.Equal("21", channels.LastChannelId);
        Assert.Equal("701", await database.ReadDeliveryLoopCursorAsync("channels", "channel:21"));
        Assert.Equal("555", await database.ReadDeliveryLoopCursorAsync("channels", "den-channels"));
        var row = await ReadSingleDeliveryAsync(databasePath);
        Assert.Equal("reviewer", row.TargetIdentity);
        Assert.Equal("21", row.ChannelId);
        Assert.Null(row.ProjectId);
    }

    [Fact]
    public async Task PollEndpointAcceptsCamelCaseChannelIdForAgentCommonsPolls()
    {
        var databasePath = CreateTempDatabasePath();
        var channels = new FakeDenChannelsClient
        {
            Events =
            [
                new ChannelEventSnapshot("702", "message_created", "21", "channel_message", "702", "channel-message:702", DateTimeOffset.Parse("2026-05-19T09:40:00Z"))
            ],
            MessagesById = new Dictionary<string, ChannelMessageSnapshot>
            {
                ["702"] = new ChannelMessageSnapshot("702", "21", "user", "Patch", "human_text", "@reviewer camel case smoke", "wake_event", "direct-agent-message:21:702", "channel-message:702", DateTimeOffset.Parse("2026-05-19T09:40:00Z"))
            },
            Memberships =
            [
                new ChannelMembershipSnapshot("21", "agent", "reviewer", "mentions_only", "active", 60, new Dictionary<string, string>())
            ]
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
                    services.AddSingleton<IDenCoreClient>(new FakeDenCoreClient());
                    services.AddSingleton<IDenChannelsClient>(channels);
                });
            });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/delivery-loop/poll", new { source = "channels", channelId = "21", limit = 5, now = "2026-05-19T09:41:00Z" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<GatewayDeliveryPollResult>();
        Assert.NotNull(body);
        Assert.Equal("completed", body.Status);
        Assert.Equal(1, body.CreatedCount);
        Assert.Equal("21", channels.LastChannelId);
        Assert.Equal(1, await CountDeliveriesAsync(databasePath));
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
        var rows = await ReadDeliveriesAsync(databasePath);
        var row = Assert.Single(rows);
        return row;
    }

    private static async Task<IReadOnlyList<DeliveryRow>> ReadDeliveriesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_kind, source_id, target_type, target_identity, delivery_mode, status,
                   dedupe_key, metadata_json, task_id, channel_id, context_summary, project_id
            FROM delivery_requests
            ORDER BY id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<DeliveryRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new DeliveryRow(
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
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    private sealed record DeliveryRow(string SourceKind, string? SourceId, string TargetType, string TargetIdentity, string DeliveryMode, string Status, string DedupeKey, string MetadataJson, int? TaskId, string? ChannelId, string? ContextSummary, string? ProjectId);

    private sealed class FakeDenCoreClient : IDenCoreClient
    {
        public bool OutboxAvailable { get; init; } = true;
        public IReadOnlyList<GatewayOutboxEvent> OutboxEvents { get; init; } = [];
        public SourceSummary? SourceSummary { get; init; }
        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));
        public Task<ClientListResult<DenProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<DenProjectSnapshot>.Available([]));
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
        public IReadOnlyDictionary<string, ChannelMessageSnapshot> MessagesById { get; init; } = new Dictionary<string, ChannelMessageSnapshot>();
        public string? LatestCursor { get; init; }
        public int ReadEventsCalls { get; private set; }
        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));
        public Task<ClientValueResult<ChannelMembershipListSnapshot>> ListProjectMembershipsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(ClientValueResult<ChannelMembershipListSnapshot>.Unavailable("not_found", "missing"));
        public Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default)
        {
            if (MessagesById.TryGetValue(channelMessageId, out var message))
            {
                return Task.FromResult(ClientValueResult<ChannelMessageSnapshot>.Available(message));
            }
            return Task.FromResult(Message is null ? ClientValueResult<ChannelMessageSnapshot>.Unavailable("not_found", "missing") : ClientValueResult<ChannelMessageSnapshot>.Available(Message));
        }
        public Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<ChannelMembershipSnapshot>.Available(Memberships.Where(m => m.ChannelId == channelId).ToArray()));
        public Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default) => Task.FromResult(ClientOperationResult.Completed("ok"));
        public Task<ChannelActivityPostResult> PostActivityEventAsync(ChannelActivityEventWrite activityEvent, CancellationToken cancellationToken = default) => Task.FromResult(ChannelActivityPostResult.Completed("1", "ok"));
        public string? LastProjectId { get; private set; }
        public string? LastChannelId { get; private set; }
        public Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, string? channelId, int limit, CancellationToken cancellationToken = default)
        {
            ReadEventsCalls++;
            LastProjectId = projectId;
            LastChannelId = channelId;
            return Task.FromResult(EventsAvailable ? ClientListResult<ChannelEventSnapshot>.Available(Events.Take(limit).ToArray()) : ClientListResult<ChannelEventSnapshot>.Unavailable("offline", "channels offline"));
        }
        public Task<ClientValueResult<string>> GetLatestChannelEventCursorAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(LatestCursor is null ? ClientValueResult<string>.Unavailable("empty_cursor", "no events") : ClientValueResult<string>.Available(LatestCursor));
    }
}
