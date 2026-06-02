namespace DenGateway.Service.Clients;

public interface IDenCoreClient
{
    Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<ClientListResult<DenProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<ClientListResult<GatewayBindingSnapshot>> ListActiveBindingsAsync(CancellationToken cancellationToken = default);
    Task<ClientValueResult<SourceSummary>> GetSourceSummaryAsync(string sourceKind, string sourceId, string? projectId, CancellationToken cancellationToken = default);
    Task<ClientListResult<GatewayOutboxEvent>> ReadEventOutboxAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default);
    Task<ClientOperationResult> PostGatewayReconciliationEventsAsync(IReadOnlyList<GatewayReconciliationEvent> events, CancellationToken cancellationToken = default);
    Task<ClientListResult<UserNotificationFeedItem>> ListUserNotificationsAsync(int? limit = null, string? projectId = null, string? after = null, CancellationToken cancellationToken = default);
}

public interface IDenChannelsClient
{
    Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<ClientValueResult<ChannelMembershipListSnapshot>> ListProjectMembershipsAsync(string projectId, CancellationToken cancellationToken = default);
    Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default);
    Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default);
    Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default);
    Task<ChannelActivityPostResult> PostActivityEventAsync(ChannelActivityEventWrite activityEvent, CancellationToken cancellationToken = default);
    Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, string? channelId, int limit, CancellationToken cancellationToken = default);
    Task<ClientValueResult<string>> GetLatestChannelEventCursorAsync(string projectId, CancellationToken cancellationToken = default);
}

public sealed class StubDenCoreClient : IDenCoreClient
{
    private readonly IReadOnlyList<GatewayBindingSnapshot> _bindings;

    public StubDenCoreClient(IReadOnlyList<GatewayBindingSnapshot> bindings)
    {
        _bindings = bindings;
    }

    public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ServiceHealthResult.Available("stub", "Den Core stub is configured."));
    }

    public Task<ClientListResult<DenProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientListResult<DenProjectSnapshot>.Available([]));
    }

    public Task<ClientListResult<GatewayBindingSnapshot>> ListActiveBindingsAsync(CancellationToken cancellationToken = default)
    {
        var active = _bindings.Where(binding => string.Equals(binding.Status, "active", StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult(ClientListResult<GatewayBindingSnapshot>.Available(active));
    }

    public Task<ClientValueResult<SourceSummary>> GetSourceSummaryAsync(string sourceKind, string sourceId, string? projectId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientValueResult<SourceSummary>.Unavailable(
            "not_implemented",
            "Den Core source-summary/deep-link contract is not implemented yet; tracked by den-mcp Gateway integration follow-up."));
    }

    public Task<ClientListResult<GatewayOutboxEvent>> ReadEventOutboxAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientListResult<GatewayOutboxEvent>.Unavailable(
            "not_implemented",
            "Den Core significant-event outbox cursor is not implemented yet; tracked by den-mcp Gateway integration follow-up."));
    }

    public Task<ClientOperationResult> PostGatewayReconciliationEventsAsync(IReadOnlyList<GatewayReconciliationEvent> events, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientOperationResult.Unavailable(
            "not_implemented",
            "Den Core Gateway sentinel reconciliation endpoint is not implemented yet; tracked by den-mcp Gateway integration follow-up."));
    }

    public Task<ClientListResult<UserNotificationFeedItem>> ListUserNotificationsAsync(int? limit = null, string? projectId = null, string? after = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientListResult<UserNotificationFeedItem>.Unavailable(
            "not_implemented",
            "Den Core user-notification feed is not polled in stub mode."));
    }
}

public sealed class StubDenChannelsClient : IDenChannelsClient
{
    private readonly IReadOnlyList<ChannelMembershipSnapshot> _memberships;

    public StubDenChannelsClient(IReadOnlyList<ChannelMembershipSnapshot> memberships)
    {
        _memberships = memberships;
    }

    public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ServiceHealthResult.Available("stub", "Den Channels stub is configured."));
    }

    public Task<ClientValueResult<ChannelMembershipListSnapshot>> ListProjectMembershipsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientValueResult<ChannelMembershipListSnapshot>.Unavailable(
            "not_implemented",
            "Project membership discovery is unavailable in stub mode; pass explicit simulation payloads for v1 tests."));
    }

    public Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientValueResult<ChannelMessageSnapshot>.Unavailable(
            "not_implemented",
            "Channel message lookup is unavailable in stub mode; pass explicit simulation payloads for v1 tests."));
    }

    public Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var memberships = _memberships.Where(membership => string.Equals(membership.ChannelId, channelId, StringComparison.Ordinal)).ToArray();
        return Task.FromResult(ClientListResult<ChannelMembershipSnapshot>.Available(memberships));
    }

    public Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientOperationResult.Unavailable(
            "not_implemented",
            "Posting Gateway mirror/system messages to Den Channels is deferred until the channel event contract matures."));
    }

    public Task<ChannelActivityPostResult> PostActivityEventAsync(ChannelActivityEventWrite activityEvent, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ChannelActivityPostResult.Unavailable(
            "not_implemented",
            "Posting activity events to Den Channels is unavailable in stub mode."));
    }

    public Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, string? channelId, int limit, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientListResult<ChannelEventSnapshot>.Unavailable(
            "not_implemented",
            "Den Channels event cursor for Gateway wake policy is not implemented yet; use explicit simulation payloads."));
    }

    public Task<ClientValueResult<string>> GetLatestChannelEventCursorAsync(string projectId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ClientValueResult<string>.Unavailable(
            "not_implemented",
            "Den Channels latest event cursor discovery is unavailable in stub mode."));
    }
}

public sealed record ServiceHealthResult(bool IsAvailable, string Mode, string Status, string? ErrorCode, string? Message)
{
    public static ServiceHealthResult Available(string mode, string message) => new(true, mode, "available", null, message);
    public static ServiceHealthResult Unavailable(string mode, string errorCode, string message) => new(false, mode, "unavailable", errorCode, message);
}

public sealed record ClientValueResult<T>(bool IsAvailable, T? Value, string? ErrorCode, string? Message)
{
    public static ClientValueResult<T> Available(T value) => new(true, value, null, null);
    public static ClientValueResult<T> Unavailable(string errorCode, string message) => new(false, default, errorCode, message);
}

public sealed record ClientListResult<T>(bool IsAvailable, IReadOnlyList<T> Items, string? ErrorCode, string? Message)
{
    public static ClientListResult<T> Available(IReadOnlyList<T> items) => new(true, items, null, null);
    public static ClientListResult<T> Unavailable(string errorCode, string message) => new(false, Array.Empty<T>(), errorCode, message);
}

public sealed record ClientOperationResult(bool IsAvailable, string Status, string? ErrorCode, string? Message)
{
    public static ClientOperationResult Completed(string message) => new(true, "completed", null, message);
    public static ClientOperationResult Unavailable(string errorCode, string message) => new(false, "unavailable", errorCode, message);
}

public sealed record ChannelActivityPostResult(bool IsAvailable, string Status, string? ActivityEventId, string? ErrorCode, string? Message)
{
    public static ChannelActivityPostResult Completed(string? activityEventId, string message) => new(true, "completed", activityEventId, null, message);
    public static ChannelActivityPostResult Unavailable(string errorCode, string message) => new(false, "unavailable", null, errorCode, message);
}

public sealed record GatewayBindingSnapshot(
    string AdapterKind,
    string AdapterInstanceId,
    string? AgentIdentity,
    string? UserIdentity,
    string? ProjectId,
    string? Role,
    string Status,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record DenProjectSnapshot(
    string ProjectId,
    string Name,
    string Kind,
    string Visibility);

public sealed record SourceSummary(
    string SourceKind,
    string SourceId,
    string? SourceProjectId,
    string Title,
    string Summary,
    string DeepLink,
    DateTimeOffset OccurredAt,
    string Actor,
    string Severity,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record GatewayOutboxEvent(
    string Cursor,
    string EventId,
    string EventType,
    string? ProjectId,
    string SourceKind,
    string SourceId,
    DateTimeOffset OccurredAt,
    string Actor,
    string SummaryHint,
    string? DeepLink,
    string Severity,
    string DedupeKey);

public sealed record GatewayReconciliationEvent(
    string EventKind,
    string? TargetIdentity,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record ChannelMessageSnapshot(
    string ChannelMessageId,
    string ChannelId,
    string SenderType,
    string SenderIdentity,
    string MessageKind,
    string Body,
    string? SourceKind,
    string? SourceId,
    string? DedupeKey,
    DateTimeOffset CreatedAt);

public sealed record ChannelMembershipSnapshot(
    string ChannelId,
    string MemberType,
    string MemberIdentity,
    string WakePolicy,
    string Status,
    int? CooldownSeconds,
    IReadOnlyDictionary<string, string> Settings);

public sealed record ChannelMembershipListSnapshot(
    string ChannelId,
    string ChannelSlug,
    string ChannelKind,
    string? ProjectId,
    IReadOnlyList<ChannelMembershipSnapshot> Members);

public sealed record ChannelMirrorMessage(
    string ChannelId,
    string MessageKind,
    string Body,
    string SourceKind,
    string SourceId,
    string? DeepLink,
    string DedupeKey,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ChannelActivityEventWrite(
    string ChannelId,
    string? ProjectId,
    string AgentIdentity,
    string? DeliveryRequestId,
    string? DisplayBlockId,
    string? HermesSessionKey,
    string? ParentHermesSessionKey,
    string? ParentAgentIdentity,
    string? WorkerRunId,
    string? WorkerRole,
    long? TaskId,
    long? ThreadId,
    long? AnchorMessageId,
    string EventType,
    string Status,
    long? Sequence,
    string? Title,
    string? Summary,
    string? PreviewJson,
    string? MetadataJson,
    string? DedupeKey);

public sealed record ChannelEventSnapshot(
    string Cursor,
    string EventType,
    string ChannelId,
    string SourceKind,
    string SourceId,
    string DedupeKey,
    DateTimeOffset OccurredAt,
    string? TargetProjectId = null,
    string? TargetTaskId = null,
    string? AssignmentId = null,
    string? RunId = null,
    string? Role = null,
    string? ProfileIdentity = null);

public sealed record UserNotificationFeedItem(
    string Id,
    string? ProjectId,
    string? TaskId,
    string? Sender,
    string? Content,
    IReadOnlyDictionary<string, string> Metadata,
    string Urgency,
    DateTimeOffset CreatedAt);
