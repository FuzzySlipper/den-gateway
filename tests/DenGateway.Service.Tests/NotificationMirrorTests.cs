using DenGateway.Service.Clients;
using DenGateway.Service.NotificationMirror;
using DenGateway.Service.Persistence;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DenGateway.Service.Tests;

public class NotificationMirrorTests
{
    private static readonly DenGatewayOptions FullOptions = new()
    {
        NotificationLaneMirror = new NotificationLaneMirrorOptions
        {
            Enabled = true,
            TargetChannelId = "lane-notifications",
            IncludedMetadataTypes = ["agent_work_complete", "blocker", "failure"],
            PollIntervalSeconds = 60,
            Limit = 50
        }
    };

    private static readonly DenGatewayOptions EmptyOptions = new()
    {
        NotificationLaneMirror = new NotificationLaneMirrorOptions()
    };

    private static readonly IOptions<DenGatewayOptions> FullOptionsWrapper = Options.Create(FullOptions);
    private static readonly IOptions<DenGatewayOptions> EmptyOptionsWrapper = Options.Create(EmptyOptions);

    [Fact]
    public async Task MirrorIsDisabledByDefaultAndReturnsNoop()
    {
        var database = CreateDatabase();
        var mirror = new GatewayNotificationMirrorService(
            database,
            new FakeDenCoreClient(),
            new FakeDenChannelsClient(),
            EmptyOptionsWrapper);

        var result = await mirror.PollAndMirrorOnceAsync();

        Assert.Equal("disabled", result.Status);
        Assert.Equal(0, result.MirroredCount);
        Assert.Equal(0, result.DuplicateCount);
    }

    [Fact]
    public async Task EnabledServiceMirrorsAgentWorkCompleteIntoConfiguredLane()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-1",
                    ProjectId: "den-gateway",
                    TaskId: "1792",
                    Sender: "den-mcp-runner",
                    Content: "Task #1792 completed: notification lane mirror",
                    Metadata: new Dictionary<string, string>
                    {
                        ["type"] = "agent_work_complete",
                        ["severity"] = "normal"
                    },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();

        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        var first = await mirror.PollAndMirrorOnceAsync();
        Assert.Equal("completed", first.Status);
        Assert.Equal(1, first.MirroredCount);
        Assert.Equal(0, first.DuplicateCount);
        Assert.Single(channels.PostedMirrorMessages);

        var msg = channels.PostedMirrorMessages[0];
        Assert.Equal("lane-notifications", msg.ChannelId);
        Assert.Equal("notification_mirror", msg.MessageKind);
        Assert.Contains("core-user-notification:notif-1:agent_work_complete", msg.DedupeKey);
        Assert.Equal("user_notification", msg.SourceKind);
        Assert.Equal("notif-1", msg.SourceId);
        Assert.True(msg.Metadata.ContainsKey("non_waking"));
        Assert.Equal("true", msg.Metadata["non_waking"]);
        Assert.True(msg.Metadata.ContainsKey("delivery_mode"));
        Assert.Equal("record_only", msg.Metadata["delivery_mode"]);
        Assert.Equal("den-gateway", msg.Metadata["sourceProjectId"]);
        Assert.Equal("1792", msg.Metadata["taskId"]);
    }

    [Fact]
    public async Task RepeatedPollsDoNotRepostSameNotification()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-2",
                    ProjectId: "den-gateway",
                    TaskId: "1792",
                    Sender: "den-mcp-runner",
                    Content: "Agent work complete",
                    Metadata: new Dictionary<string, string>
                    {
                        ["type"] = "agent_work_complete"
                    },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();

        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        var first = await mirror.PollAndMirrorOnceAsync();
        Assert.Equal(1, first.MirroredCount);

        // Cursor advanced past notif-2; second poll finds nothing new
        core.Notifications =
        [
            new UserNotificationFeedItem(
                Id: "notif-3",
                ProjectId: "den-gateway",
                TaskId: "1792",
                Sender: "den-mcp-runner",
                Content: "Newer notification",
                Metadata: new Dictionary<string, string> { ["type"] = "blocker" },
                Urgency: "normal",
                CreatedAt: DateTimeOffset.Parse("2026-05-31T12:01:00Z"))
        ];

        var second = await mirror.PollAndMirrorOnceAsync();
        Assert.Equal("completed", second.Status);
        Assert.Equal(1, second.MirroredCount); // only the new notification
        Assert.Equal(0, second.DuplicateCount);
        Assert.Equal(2, channels.PostedMirrorMessages.Count); // both posts are distinct
        Assert.Equal("notif-2", channels.PostedMirrorMessages[0].SourceId);
        Assert.Equal("notif-3", channels.PostedMirrorMessages[1].SourceId);
    }

    [Fact]
    public async Task DedupeKeyPreventsRepostOfSameNotificationTypeEvenIfCursorReset()
    {
        // Deduplication at the Channels level: same dedupe key should not produce
        // a successful second post. Since our FakeDenChannelsClient always succeeds,
        // we verify that the service constructs a deterministic dedupe key based on
        // notification id + metadata type.
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-4",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Test",
                    Metadata: new Dictionary<string, string> { ["type"] = "agent_work_complete" },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();
        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        await mirror.PollAndMirrorOnceAsync();

        var msg = Assert.Single(channels.PostedMirrorMessages);
        Assert.Equal("core-user-notification:notif-4:agent_work_complete", msg.DedupeKey);
        Assert.Equal("user_notification", msg.SourceKind);
        Assert.Equal("notif-4", msg.SourceId);
    }

    [Fact]
    public async Task NonWakeDefaultMetadataIsPresentOnAllMirroredMessages()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-3",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Blocker detected",
                    Metadata: new Dictionary<string, string>
                    {
                        ["type"] = "blocker"
                    },
                    Urgency: "high",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();
        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        await mirror.PollAndMirrorOnceAsync();

        var msg = Assert.Single(channels.PostedMirrorMessages);
        Assert.Equal("true", msg.Metadata["non_waking"]);
        Assert.Equal("record_only", msg.Metadata["delivery_mode"]);
        Assert.Equal("notification_mirror", msg.Metadata["mirror_kind"]);
        Assert.Equal("high", msg.Metadata["urgency"]);
    }

    [Fact]
    public async Task OnlyMatchingMetadataTypesAreMirrored()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-match",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Agent work complete",
                    Metadata: new Dictionary<string, string> { ["type"] = "agent_work_complete" },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z")),
                new UserNotificationFeedItem(
                    Id: "notif-skip",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "General info",
                    Metadata: new Dictionary<string, string> { ["type"] = "info" },
                    Urgency: "low",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z")),
                new UserNotificationFeedItem(
                    Id: "notif-failure",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Failure detected",
                    Metadata: new Dictionary<string, string> { ["type"] = "failure" },
                    Urgency: "high",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();
        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        await mirror.PollAndMirrorOnceAsync();

        Assert.Equal(2, channels.PostedMirrorMessages.Count);
        Assert.Contains(channels.PostedMirrorMessages, m => m.SourceId == "notif-match");
        Assert.Contains(channels.PostedMirrorMessages, m => m.SourceId == "notif-failure");
        Assert.DoesNotContain(channels.PostedMirrorMessages, m => m.SourceId == "notif-skip");
    }

    [Fact]
    public async Task MissingTargetChannelConfigSuppressesMirrorGracefully()
    {
        var db = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-4",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Test",
                    Metadata: new Dictionary<string, string> { ["type"] = "agent_work_complete" },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();

        var noChannelOptions = new DenGatewayOptions
        {
            NotificationLaneMirror = new NotificationLaneMirrorOptions
            {
                Enabled = true,
                TargetChannelId = ""
            }
        };

        var mirror = new GatewayNotificationMirrorService(db, core, channels, Options.Create(noChannelOptions));
        var result = await mirror.PollAndMirrorOnceAsync();

        Assert.Equal("degraded", result.Status);
        Assert.Equal(0, result.MirroredCount);
        Assert.Empty(channels.PostedMirrorMessages);
    }

    [Fact]
    public async Task MalformedMetadataDoesNotThrowAndIsSkipped()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-malformed",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Test",
                    Metadata: new Dictionary<string, string>(), // missing "type" key
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z")),
                new UserNotificationFeedItem(
                    Id: "notif-valid",
                    ProjectId: "den-gateway",
                    TaskId: "1792",
                    Sender: "den-mcp-runner",
                    Content: "Valid notification",
                    Metadata: new Dictionary<string, string> { ["type"] = "blocker" },
                    Urgency: "high",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();
        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        var result = await mirror.PollAndMirrorOnceAsync();

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.MirroredCount); // only the valid one
        Assert.Single(channels.PostedMirrorMessages);
        Assert.Equal("notif-valid", channels.PostedMirrorMessages[0].SourceId);
    }

    [Fact]
    public async Task ChangingTargetChannelReflectsInPostedMessages()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-5",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Test",
                    Metadata: new Dictionary<string, string> { ["type"] = "agent_work_complete" },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channelsA = new FakeDenChannelsClient();

        var optionsA = new DenGatewayOptions
        {
            NotificationLaneMirror = new NotificationLaneMirrorOptions
            {
                Enabled = true,
                TargetChannelId = "lane-alpha"
            }
        };
        var mirrorA = new GatewayNotificationMirrorService(database, core, channelsA, Options.Create(optionsA));
        await mirrorA.PollAndMirrorOnceAsync();

        Assert.Single(channelsA.PostedMirrorMessages);
        Assert.Equal("lane-alpha", channelsA.PostedMirrorMessages[0].ChannelId);

        // Switch channel
        var channelsB = new FakeDenChannelsClient();
        var optionsB = new DenGatewayOptions
        {
            NotificationLaneMirror = new NotificationLaneMirrorOptions
            {
                Enabled = true,
                TargetChannelId = "lane-beta"
            }
        };
        core.Notifications =
        [
            new UserNotificationFeedItem(
                Id: "notif-5",
                ProjectId: "den-gateway",
                TaskId: null,
                Sender: "den-mcp-runner",
                Content: "Test",
                Metadata: new Dictionary<string, string> { ["type"] = "agent_work_complete" },
                Urgency: "normal",
                CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
        ];

        // Fresh database so cursor is reset
        var databaseB = CreateDatabase();
        var mirrorB = new GatewayNotificationMirrorService(databaseB, core, channelsB, Options.Create(optionsB));
        await mirrorB.PollAndMirrorOnceAsync();

        Assert.Single(channelsB.PostedMirrorMessages);
        Assert.Equal("lane-beta", channelsB.PostedMirrorMessages[0].ChannelId);
    }

    [Fact]
    public async Task CanonicalRefsArePresentInMirrorMessageMetadata()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-canon",
                    ProjectId: "den-mcp",
                    TaskId: "500",
                    Sender: "den-mcp-runner",
                    Content: "Work done",
                    Metadata: new Dictionary<string, string> { ["type"] = "agent_work_complete" },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient();
        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        await mirror.PollAndMirrorOnceAsync();

        var msg = Assert.Single(channels.PostedMirrorMessages);
        Assert.Equal("user_notification", msg.SourceKind);
        Assert.Equal("notif-canon", msg.SourceId);
        Assert.Equal("den-mcp", msg.Metadata["sourceProjectId"]);
        Assert.Equal("500", msg.Metadata["taskId"]);
        Assert.Equal("Work done", msg.Metadata["content"]);
        Assert.Equal("den-mcp-runner", msg.Metadata["sender"]);
        Assert.Equal("notif-canon", msg.Metadata["notificationId"]);
    }

    [Fact]
    public async Task CoreUnavailableReturnsDegraded()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient { NotificationsAvailable = false };
        var channels = new FakeDenChannelsClient();
        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        var result = await mirror.PollAndMirrorOnceAsync();

        Assert.Equal("degraded", result.Status);
        Assert.Equal(0, result.MirroredCount);
    }

    [Fact]
    public async Task ChannelsPostFailureRecordsSkippedAndContinues()
    {
        var database = CreateDatabase();
        var core = new FakeDenCoreClient
        {
            Notifications =
            [
                new UserNotificationFeedItem(
                    Id: "notif-err",
                    ProjectId: "den-gateway",
                    TaskId: null,
                    Sender: "den-mcp-runner",
                    Content: "Test",
                    Metadata: new Dictionary<string, string> { ["type"] = "agent_work_complete" },
                    Urgency: "normal",
                    CreatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
            ]
        };
        var channels = new FakeDenChannelsClient { PostAvailable = false };
        var mirror = new GatewayNotificationMirrorService(database, core, channels, FullOptionsWrapper);

        var result = await mirror.PollAndMirrorOnceAsync();

        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.MirroredCount);
        Assert.True(result.SkippedCount > 0);
    }

    private static GatewayDatabase CreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), "den-gateway-tests", "notification-mirror", Guid.NewGuid().ToString("N"), "den-gateway.db");
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var db = new GatewayDatabase(path);
        db.InitializeAsync().GetAwaiter().GetResult();
        return db;
    }

    private sealed class FakeDenCoreClient : IDenCoreClient
    {
        public bool NotificationsAvailable { get; init; } = true;
        public IReadOnlyList<UserNotificationFeedItem> Notifications { get; set; } = [];
        public bool OutboxAvailable { get; init; } = true;
        public IReadOnlyList<GatewayOutboxEvent> OutboxEvents { get; init; } = [];
        public SourceSummary? SourceSummary { get; init; }

        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));

        public Task<ClientListResult<DenProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ClientListResult<DenProjectSnapshot>.Available([]));

        public Task<ClientListResult<GatewayBindingSnapshot>> ListActiveBindingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ClientListResult<GatewayBindingSnapshot>.Available([]));

        public Task<ClientValueResult<SourceSummary>> GetSourceSummaryAsync(string sourceKind, string sourceId, string? projectId, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientValueResult<SourceSummary>.Unavailable("not_found", "missing"));

        public Task<ClientListResult<GatewayOutboxEvent>> ReadEventOutboxAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientListResult<GatewayOutboxEvent>.Available([]));

        public Task<ClientOperationResult> PostGatewayReconciliationEventsAsync(IReadOnlyList<GatewayReconciliationEvent> events, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientOperationResult.Completed("ok"));

        public Task<ClientListResult<UserNotificationFeedItem>> ListUserNotificationsAsync(int? limit, string? projectId, string? after, CancellationToken cancellationToken = default)
        {
            if (!NotificationsAvailable)
            {
                return Task.FromResult(ClientListResult<UserNotificationFeedItem>.Unavailable("offline", "Core unavailable"));
            }

            var items = Notifications.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(after))
            {
                items = items.Where(n => string.Compare(n.Id, after, StringComparison.Ordinal) > 0);
            }

            return Task.FromResult(ClientListResult<UserNotificationFeedItem>.Available(items.Take(limit ?? 50).ToArray()));
        }
    }

    private sealed class FakeDenChannelsClient : IDenChannelsClient
    {
        public bool PostAvailable { get; init; } = true;
        public List<ChannelMirrorMessage> PostedMirrorMessages { get; } = [];

        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));

        public Task<ClientValueResult<ChannelMembershipListSnapshot>> ListProjectMembershipsAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientValueResult<ChannelMembershipListSnapshot>.Unavailable("not_found", "missing"));

        public Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientValueResult<ChannelMessageSnapshot>.Unavailable("not_found", "missing"));

        public Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientListResult<ChannelMembershipSnapshot>.Available([]));

        public Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default)
        {
            if (PostAvailable)
            {
                PostedMirrorMessages.Add(message);
                return Task.FromResult(ClientOperationResult.Completed("ok"));
            }

            return Task.FromResult(ClientOperationResult.Unavailable("offline", "Channels unavailable"));
        }

        public Task<ChannelActivityPostResult> PostActivityEventAsync(ChannelActivityEventWrite activityEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(ChannelActivityPostResult.Completed("1", "ok"));

        public Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, string? channelId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientListResult<ChannelEventSnapshot>.Available([]));

        public Task<ClientValueResult<string>> GetLatestChannelEventCursorAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult(ClientValueResult<string>.Unavailable("empty_cursor", "no events"));
    }
}
