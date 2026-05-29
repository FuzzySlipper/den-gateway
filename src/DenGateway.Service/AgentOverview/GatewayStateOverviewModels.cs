using System.Text.Json.Serialization;

namespace DenGateway.Service.AgentOverview;

public sealed record GatewayStateOverviewRequest(
    string? ProjectId = null,
    string? AgentIdentity = null,
    string? Role = null,
    int IncludeTerminalMinutes = 120,
    int Limit = 200);

public sealed class GatewayStateOverviewResponse
{
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("service")]
    public string Service { get; init; } = "den-gateway";

    [JsonPropertyName("bindingHealth")]
    public required GatewayBindingHealth BindingHealth { get; init; }

    [JsonPropertyName("agents")]
    public required IReadOnlyList<GatewayStateGroup> Agents { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("metadata")]
    public required GatewayStateOverviewMetadata Metadata { get; init; }

    [JsonIgnore]
    public IReadOnlyList<GatewayStateGroup> Groups => Agents;
}

public sealed record GatewayBindingHealth(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("freshCount")] int FreshCount,
    [property: JsonPropertyName("staleCount")] int StaleCount,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record GatewayStateOverviewMetadata(
    [property: JsonPropertyName("totalGroups")] int TotalGroups,
    [property: JsonPropertyName("totalBindings")] int TotalBindings,
    [property: JsonPropertyName("totalDeliveries")] int TotalDeliveries,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("includeTerminalMinutes")] int IncludeTerminalMinutes);

public sealed record GatewayStateGroup(
    [property: JsonPropertyName("agentKey")] string AgentKey,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("agentIdentity")] string? AgentIdentity,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("bindingFreshness")] string BindingFreshness,
    [property: JsonPropertyName("adapterInstances")] IReadOnlyList<GatewayAdapterInstanceInfo> AdapterInstances,
    [property: JsonPropertyName("deliverySummary")] DeliverySummaryCounts DeliveryCounts,
    [property: JsonPropertyName("currentDeliveries")] IReadOnlyList<GatewayDeliveryOverview> CurrentDeliveries,
    [property: JsonPropertyName("recentDeliveries")] IReadOnlyList<GatewayDeliveryOverview> RecentDeliveries,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags)
{
    [JsonIgnore]
    public string Classification => DeliveryCounts.State;

    [JsonIgnore]
    public GatewayAdapterInstanceInfo? Binding => AdapterInstances.Count > 0 ? AdapterInstances[0] : null;

    [JsonIgnore]
    public IReadOnlyList<string>? Warnings => Flags.Count > 0 ? Flags : null;
}

public sealed record GatewayAdapterInstanceInfo(
    [property: JsonPropertyName("adapterKind")] string AdapterKind,
    [property: JsonPropertyName("adapterInstanceId")] string AdapterInstanceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("isStale")] bool IsStale,
    [property: JsonPropertyName("stalenessReason")] string? StalenessReason,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string> Metadata)
{
    [JsonIgnore]
    public long Id { get; init; }

    [JsonIgnore]
    public bool IsFresh => !IsStale;

    [JsonIgnore]
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record DeliverySummaryCounts(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("pendingCount")] int Pending,
    [property: JsonPropertyName("deliveringCount")] int Delivering,
    [property: JsonPropertyName("deliveredNotCompletedCount")] int DeliveredNotCompleted,
    [property: JsonPropertyName("completedRecentCount")] int CompletedRecent,
    [property: JsonPropertyName("failedRecentCount")] int FailedRecent,
    [property: JsonPropertyName("suppressedRecentCount")] int SuppressedRecent,
    [property: JsonPropertyName("stuckCount")] int Stuck,
    [property: JsonPropertyName("total")] int Total);

public sealed record GatewayDeliveryOverview(
    [property: JsonPropertyName("deliveryRequestId")] long DeliveryRequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("deliveryMode")] string DeliveryMode,
    [property: JsonPropertyName("targetType")] string TargetType,
    [property: JsonPropertyName("targetIdentity")] string TargetIdentity,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("taskId")] long? TaskId,
    [property: JsonPropertyName("channelId")] string? ChannelId,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("sourceId")] string? SourceId,
    [property: JsonPropertyName("sourceProjectId")] string? SourceProjectId,
    [property: JsonPropertyName("contextSummary")] string? ContextSummary,
    [property: JsonPropertyName("contextLink")] string? ContextLink,
    [property: JsonPropertyName("attemptCount")] int AttemptCount,
    [property: JsonPropertyName("leaseExpiresAt")] DateTimeOffset? LeaseExpiresAt,
    [property: JsonPropertyName("nextAttemptAt")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("lastAttempt")] GatewayDeliveryAttemptOverview? LastAttempt,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("assignmentId")] string? AssignmentId = null,
    [property: JsonPropertyName("workerIdentity")] string? WorkerIdentity = null,
    [property: JsonPropertyName("workerRole")] string? WorkerRole = null,
    [property: JsonPropertyName("assignmentPurpose")] string? AssignmentPurpose = null);

public sealed record GatewayDeliveryAttemptOverview(
    [property: JsonPropertyName("attemptId")] long AttemptId,
    [property: JsonPropertyName("attemptNumber")] int AttemptNumber,
    [property: JsonPropertyName("adapterBindingId")] long? AdapterBindingId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("ackKind")] string? AckKind,
    [property: JsonPropertyName("externalMessageId")] string? ExternalMessageId,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("observedAt")] DateTimeOffset? ObservedAt,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage);
