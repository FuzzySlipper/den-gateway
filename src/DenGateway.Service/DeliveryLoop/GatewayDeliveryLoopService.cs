using System.Text.Json;
using System.Text.Json.Serialization;
using DenGateway.Service.Clients;
using DenGateway.Service.Persistence;

namespace DenGateway.Service.DeliveryLoop;

public sealed class GatewayDeliveryLoopService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GatewayDatabase _database;
    private readonly IDenCoreClient _denCoreClient;
    private readonly IDenChannelsClient _denChannelsClient;

    public GatewayDeliveryLoopService(GatewayDatabase database, IDenCoreClient denCoreClient, IDenChannelsClient denChannelsClient)
    {
        _database = database;
        _denCoreClient = denCoreClient;
        _denChannelsClient = denChannelsClient;
    }

    public async Task<GatewayDeliveryPollResult> PollOnceAsync(GatewayDeliveryPollRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = string.IsNullOrWhiteSpace(request.Source) ? "all" : request.Source.Trim().ToLowerInvariant();
        var now = request.Now ?? DateTimeOffset.UtcNow;
        var limit = Math.Clamp(request.Limit <= 0 ? 50 : request.Limit, 1, 200);
        var aggregate = new PollAccumulator();

        if (source is "all" or "core")
        {
            var core = await PollCoreOnceAsync(request.ProjectId, limit, now, cancellationToken);
            aggregate.Add(core);
        }

        if (source is "all" or "channels")
        {
            var channels = await PollChannelsOnceAsync(request.ProjectId, limit, now, cancellationToken);
            aggregate.Add(channels);
        }

        if (source is not ("all" or "core" or "channels"))
        {
            return new GatewayDeliveryPollResult("rejected", 0, 0, 0, 0, null, "unsupported_source", $"Unsupported delivery-loop source '{request.Source}'.");
        }

        return aggregate.ToResult();
    }

    private async Task<GatewayDeliveryPollResult> PollCoreOnceAsync(string? projectId, int limit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var after = await _database.ReadDeliveryLoopCursorAsync("core", projectId, cancellationToken);
        var outbox = await _denCoreClient.ReadEventOutboxAsync(after: after, projectId: projectId, limit: limit, cancellationToken);
        if (!outbox.IsAvailable)
        {
            return GatewayDeliveryPollResult.Degraded("core_unavailable", outbox.Message ?? "Den Core outbox is unavailable.");
        }

        var seen = 0;
        var created = 0;
        var duplicates = 0;
        var suppressed = 0;
        string? nextCursor = null;
        foreach (var item in outbox.Items)
        {
            seen++;
            nextCursor = item.Cursor;
            var summaryResult = await _denCoreClient.GetSourceSummaryAsync(item.SourceKind, item.SourceId, item.ProjectId ?? projectId, cancellationToken);
            var summary = summaryResult.Value;
            var metadata = summary?.Metadata ?? new Dictionary<string, string>();
            if (!metadata.TryGetValue("targetIdentity", out var targetIdentity) || string.IsNullOrWhiteSpace(targetIdentity))
            {
                suppressed++;
                continue;
            }

            var targetType = GetMetadata(metadata, "targetType", "agent");
            var deliveryMode = GetMetadata(metadata, "deliveryMode", "wake");
            var dedupeKey = string.IsNullOrWhiteSpace(item.DedupeKey) ? $"core:{item.EventId}" : item.DedupeKey;
            var create = await _database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
                SourceKind: item.SourceKind,
                SourceId: item.SourceId,
                SourceProjectId: summary?.SourceProjectId ?? item.ProjectId,
                TargetType: targetType,
                TargetIdentity: targetIdentity,
                ProjectId: item.ProjectId ?? projectId,
                TaskId: ParseNullableInt(metadata.TryGetValue("taskId", out var taskId) ? taskId : null),
                ChannelId: metadata.TryGetValue("channelId", out var channelId) ? channelId : null,
                DeliveryMode: deliveryMode,
                Priority: ParseNullableInt(metadata.TryGetValue("priority", out var priority) ? priority : null) ?? 3,
                Reason: metadata.TryGetValue("reason", out var reason) ? reason : item.EventType,
                ContextSummary: summary?.Summary ?? item.SummaryHint,
                ContextLink: summary?.DeepLink ?? item.DeepLink,
                MetadataJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["source"] = "core",
                    ["event_id"] = item.EventId,
                    ["event_type"] = item.EventType,
                    ["actor"] = item.Actor,
                    ["severity"] = item.Severity,
                    ["occurred_at"] = item.OccurredAt,
                    ["summary_metadata"] = metadata
                }, JsonOptions),
                Status: "pending",
                SuppressionReason: null,
                DedupeKey: dedupeKey,
                CascadeDepth: 0,
                NextAttemptAt: null,
                ExpiresAt: null,
                CreatedAt: now), cancellationToken);

            if (create.AlreadyExisted)
            {
                duplicates++;
            }
            else
            {
                created++;
            }
        }

        if (nextCursor is not null)
        {
            await _database.UpsertDeliveryLoopCursorAsync("core", projectId, nextCursor, now, cancellationToken);
        }

        return new GatewayDeliveryPollResult("completed", seen, created, duplicates, suppressed, nextCursor, null, null);
    }

    private async Task<GatewayDeliveryPollResult> PollChannelsOnceAsync(string? projectId, int limit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var after = await _database.ReadDeliveryLoopCursorAsync("channels", projectId, cancellationToken);
        var events = await _denChannelsClient.ReadChannelEventsAsync(after: after, projectId: projectId, limit: limit, cancellationToken);
        if (!events.IsAvailable)
        {
            return GatewayDeliveryPollResult.Degraded("channels_unavailable", events.Message ?? "Den Channels event cursor is unavailable.");
        }

        var seen = 0;
        var created = 0;
        var duplicates = 0;
        var suppressed = 0;
        string? nextCursor = null;
        foreach (var channelEvent in events.Items)
        {
            seen++;
            nextCursor = channelEvent.Cursor;
            var messageResult = await _denChannelsClient.GetChannelMessageAsync(channelEvent.SourceId, cancellationToken);
            var membershipsResult = await _denChannelsClient.ListMembershipsAsync(channelEvent.ChannelId, cancellationToken);
            if (!membershipsResult.IsAvailable)
            {
                suppressed++;
                continue;
            }

            var message = messageResult.Value;
            foreach (var membership in membershipsResult.Items)
            {
                if (!string.Equals(membership.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (message is not null
                    && string.Equals(membership.MemberType, message.SenderType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(membership.MemberIdentity, message.SenderIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    suppressed++;
                    continue;
                }

                var deliveryMode = WakePolicyToDeliveryMode(membership.WakePolicy);
                if (deliveryMode is null)
                {
                    continue;
                }

                var targetType = NormalizeMemberType(membership.MemberType);
                var dedupeKey = $"{channelEvent.DedupeKey}:{targetType}:{membership.MemberIdentity}";
                var create = await _database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
                    SourceKind: channelEvent.SourceKind,
                    SourceId: channelEvent.SourceId,
                    SourceProjectId: projectId,
                    TargetType: targetType,
                    TargetIdentity: membership.MemberIdentity,
                    ProjectId: membership.Settings.TryGetValue("projectId", out var memberProjectId) && !string.IsNullOrWhiteSpace(memberProjectId) ? memberProjectId : projectId,
                    TaskId: null,
                    ChannelId: channelEvent.ChannelId,
                    DeliveryMode: deliveryMode,
                    Priority: 3,
                    Reason: channelEvent.EventType,
                    ContextSummary: message?.Body ?? $"Channel event {channelEvent.SourceId}",
                    ContextLink: $"den://channel/{channelEvent.ChannelId}/message/{channelEvent.SourceId}",
                    MetadataJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["source"] = "channels",
                        ["cursor"] = channelEvent.Cursor,
                        ["event_type"] = channelEvent.EventType,
                        ["channel_id"] = channelEvent.ChannelId,
                        ["message_kind"] = message?.MessageKind,
                        ["sender_type"] = message?.SenderType,
                        ["sender_identity"] = message?.SenderIdentity,
                        ["wake_policy"] = membership.WakePolicy
                    }, JsonOptions),
                    Status: "pending",
                    SuppressionReason: null,
                    DedupeKey: dedupeKey,
                    CascadeDepth: 0,
                    NextAttemptAt: null,
                    ExpiresAt: null,
                    CreatedAt: now), cancellationToken);

                if (create.AlreadyExisted)
                {
                    duplicates++;
                }
                else
                {
                    created++;
                }
            }
        }

        if (nextCursor is not null)
        {
            await _database.UpsertDeliveryLoopCursorAsync("channels", projectId, nextCursor, now, cancellationToken);
        }

        return new GatewayDeliveryPollResult("completed", seen, created, duplicates, suppressed, nextCursor, null, null);
    }

    private static string GetMetadata(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? WakePolicyToDeliveryMode(string wakePolicy)
    {
        return wakePolicy.ToLowerInvariant() switch
        {
            "wake" => "wake",
            "notify" => "notify",
            "record_only" => null,
            "never" => null,
            _ => null
        };
    }

    private static string NormalizeMemberType(string memberType)
    {
        return memberType.ToLowerInvariant() switch
        {
            "agent" => "agent",
            "role" => "role",
            "instance" => "instance",
            "adapter" => "adapter",
            "user" => "user",
            _ => "agent"
        };
    }

    private sealed class PollAccumulator
    {
        private string _status = "completed";
        private string? _errorCode;
        private string? _message;
        private string? _nextCursor;
        private int _seen;
        private int _created;
        private int _duplicates;
        private int _suppressed;

        public void Add(GatewayDeliveryPollResult result)
        {
            _seen += result.SeenCount;
            _created += result.CreatedCount;
            _duplicates += result.DuplicateCount;
            _suppressed += result.SuppressedCount;
            _nextCursor = result.NextCursor ?? _nextCursor;
            if (result.Status == "degraded")
            {
                _status = "degraded";
                _errorCode ??= result.ErrorCode;
                _message ??= result.Message;
            }
        }

        public GatewayDeliveryPollResult ToResult() => new(_status, _seen, _created, _duplicates, _suppressed, _nextCursor, _errorCode, _message);
    }
}

public sealed record GatewayDeliveryPollRequest(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("now")] DateTimeOffset? Now = null);

public sealed record GatewayDeliveryPollResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("seen_count")] int SeenCount,
    [property: JsonPropertyName("created_count")] int CreatedCount,
    [property: JsonPropertyName("duplicate_count")] int DuplicateCount,
    [property: JsonPropertyName("suppressed_count")] int SuppressedCount,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("message")] string? Message)
{
    public static GatewayDeliveryPollResult Degraded(string errorCode, string message) => new("degraded", 0, 0, 0, 0, null, errorCode, message);
}
