using System.Text.Json;
using System.Text.Json.Serialization;
using DenGateway.Service.Clients;
using DenGateway.Service.Deliveries;
using DenGateway.Service.Persistence;
using Microsoft.Extensions.Options;

namespace DenGateway.Service.DeliveryLoop;

public sealed class GatewayDeliveryLoopService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GatewayDatabase _database;
    private readonly IDenCoreClient _denCoreClient;
    private readonly IDenChannelsClient _denChannelsClient;
    private readonly DeliveryPolicyOptions _policyOptions;

    public GatewayDeliveryLoopService(GatewayDatabase database, IDenCoreClient denCoreClient, IDenChannelsClient denChannelsClient, IOptions<DeliveryPolicyOptions>? policyOptions = null)
    {
        _database = database;
        _denCoreClient = denCoreClient;
        _denChannelsClient = denChannelsClient;
        _policyOptions = policyOptions?.Value ?? new DeliveryPolicyOptions();
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

        var channelId = request.GetChannelId();
        if (source is "all" or "channels")
        {
            var channels = await PollChannelsOnceAsync(request.ProjectId, channelId, limit, now, request.SeedCursorAtLatestWhenMissing, cancellationToken);
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
            if (string.IsNullOrWhiteSpace(item.SourceKind) || string.IsNullOrWhiteSpace(item.SourceId))
            {
                suppressed++;
                continue;
            }

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

    private async Task<GatewayDeliveryPollResult> PollChannelsOnceAsync(string? projectId, string? channelId, int limit, DateTimeOffset now, bool seedCursorAtLatestWhenMissing, CancellationToken cancellationToken)
    {
        var cursorScopeKey = GetChannelsCursorScopeKey(projectId, channelId);
        var after = await _database.ReadDeliveryLoopCursorAsync("channels", cursorScopeKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(channelId) && !string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(after) && seedCursorAtLatestWhenMissing)
        {
            var seed = await _denChannelsClient.GetLatestChannelEventCursorAsync(projectId, cancellationToken);
            if (seed.IsAvailable && !string.IsNullOrWhiteSpace(seed.Value))
            {
                await _database.UpsertDeliveryLoopCursorAsync("channels", cursorScopeKey, seed.Value, now, cancellationToken);
                return new GatewayDeliveryPollResult("completed", 0, 0, 0, 0, seed.Value, null, "seeded_new_project_cursor_at_latest");
            }
        }

        var events = await _denChannelsClient.ReadChannelEventsAsync(after: after, projectId: projectId, channelId: channelId, limit: limit, cancellationToken);
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
            var messageResult = await _denChannelsClient.GetChannelMessageAsync(channelEvent.Cursor, cancellationToken);
            var membershipsResult = await _denChannelsClient.ListMembershipsAsync(channelEvent.ChannelId, cancellationToken);
            if (!membershipsResult.IsAvailable)
            {
                suppressed++;
                continue;
            }

            var message = messageResult.Value;
            var directAgentTargetIdentity = TryGetDirectAgentTargetIdentity(channelEvent, message);
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

                if (!string.IsNullOrWhiteSpace(directAgentTargetIdentity)
                    && !string.Equals(membership.MemberIdentity, directAgentTargetIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    suppressed++;
                    continue;
                }

                var deliveryDecision = ResolveMembershipDeliveryMode(membership, message, channelEvent, directAgentTargetIdentity);
                if (deliveryDecision.DeliveryMode is null)
                {
                    if (deliveryDecision.CountAsSuppressed)
                    {
                        suppressed++;
                    }
                    continue;
                }

                var targetType = NormalizeMemberType(membership.MemberType);
                var dedupeKey = $"{channelEvent.DedupeKey}:{targetType}:{membership.MemberIdentity}";
                var cascadeDepth = CalculateCascadeDepth(message);

                // --- Configurable DeliveryPolicy evaluation ---
                // Use the already-resolved delivery mode as the "wake policy"
                // since ResolveMembershipDeliveryMode has already applied
                // the membership wake policy rules. This avoids duplicate
                // suppression from DeliveryPolicy's own wake-policy checks.
                var deliveryInput = new DeliverySimulationInput(
                    SourceKind: channelEvent.SourceKind,
                    SourceMessageKind: message?.MessageKind ?? channelEvent.EventType,
                    SenderType: message?.SenderType ?? membership.MemberType,
                    SenderIdentity: message?.SenderIdentity ?? membership.MemberIdentity,
                    TargetType: targetType,
                    TargetIdentity: membership.MemberIdentity,
                    DeliveryMode: deliveryDecision.DeliveryMode,
                    WakePolicy: deliveryDecision.DeliveryMode, // already resolved
                    GatewayState: "normal",
                    HasExplicitMention: MentionsMember(message, membership.MemberIdentity),
                    DedupeAlreadySeen: false,
                    TargetInCooldown: false,
                    AutoReplyWindowExceeded: false,
                    CascadeDepth: cascadeDepth,
                    MaxCascadeDepth: _policyOptions.MaxCascadeDepth,
                    AgentTennisWithoutHumanReset: IsAgentTennisWithoutHumanReset(message),
                    AmbiguousTarget: false,
                    HasActiveBinding: true,
                    SourceExpired: false,
                    ChannelId: channelEvent.ChannelId,
                    ChannelSlug: null);
                var policyDecision = DeliveryPolicy.Evaluate(deliveryInput, _policyOptions);
                if (!policyDecision.ShouldDeliver)
                {
                    // Insert a durable suppressed row for diagnostics instead of
                    // silently incrementing the counter. Same dedupe/source/target/
                    // context metadata as a pending row, with Status='suppressed'
                    // and metadata recording the policy decision details.
                    var suppressedDedupeKey = $"suppressed:{dedupeKey}:{policyDecision.SuppressionReason ?? "unknown"}";
                    var suppressedMetadata = new Dictionary<string, object?>
                    {
                        ["source"] = "channels",
                        ["cursor"] = channelEvent.Cursor,
                        ["event_type"] = channelEvent.EventType,
                        ["channel_id"] = channelEvent.ChannelId,
                        ["message_kind"] = message?.MessageKind,
                        ["sender_type"] = message?.SenderType,
                        ["sender_identity"] = message?.SenderIdentity,
                        ["wake_policy"] = membership.WakePolicy,
                        ["direct_agent_target"] = directAgentTargetIdentity,
                        ["cascade_depth"] = cascadeDepth,
                        ["applied_policy_label"] = policyDecision.AppliedPolicyLabel,
                        ["applied_override_key"] = policyDecision.AppliedOverrideKey
                    };
                    var suppressedCreate = await _database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
                        SourceKind: channelEvent.SourceKind,
                        SourceId: channelEvent.SourceId,
                        SourceProjectId: projectId,
                        TargetType: targetType,
                        TargetIdentity: membership.MemberIdentity,
                        ProjectId: membership.Settings.TryGetValue("projectId", out var memberProjectId) && !string.IsNullOrWhiteSpace(memberProjectId) ? memberProjectId : projectId,
                        TaskId: null,
                        ChannelId: channelEvent.ChannelId,
                        DeliveryMode: deliveryDecision.DeliveryMode,
                        Priority: 3,
                        Reason: channelEvent.EventType,
                        ContextSummary: message?.Body ?? $"Channel event {channelEvent.SourceId}",
                        ContextLink: $"den://channel/{channelEvent.ChannelId}/message/{channelEvent.Cursor}",
                        MetadataJson: JsonSerializer.Serialize(suppressedMetadata, JsonOptions),
                        Status: "suppressed",
                        SuppressionReason: policyDecision.SuppressionReason,
                        DedupeKey: suppressedDedupeKey,
                        CascadeDepth: cascadeDepth,
                        NextAttemptAt: null,
                        ExpiresAt: null,
                        CreatedAt: now
                    ), cancellationToken);

                    if (suppressedCreate.AlreadyExisted)
                    {
                        duplicates++;
                    }
                    else
                    {
                        suppressed++;
                    }
                    continue;
                }

                var channelMetadata = new Dictionary<string, object?>
                {
                    ["source"] = "channels",
                    ["cursor"] = channelEvent.Cursor,
                    ["event_type"] = channelEvent.EventType,
                    ["channel_id"] = channelEvent.ChannelId,
                    ["message_kind"] = message?.MessageKind,
                    ["sender_type"] = message?.SenderType,
                    ["sender_identity"] = message?.SenderIdentity,
                    ["wake_policy"] = membership.WakePolicy,
                    ["direct_agent_target"] = directAgentTargetIdentity,
                    ["cascade_depth"] = cascadeDepth,
                    ["applied_policy_label"] = policyDecision.AppliedPolicyLabel,
                    ["applied_override_key"] = policyDecision.AppliedOverrideKey
                };
                var create = await _database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
                    SourceKind: channelEvent.SourceKind,
                    SourceId: channelEvent.SourceId,
                    SourceProjectId: projectId,
                    TargetType: targetType,
                    TargetIdentity: membership.MemberIdentity,
                    ProjectId: membership.Settings.TryGetValue("projectId", out var memberProjectId2) && !string.IsNullOrWhiteSpace(memberProjectId2) ? memberProjectId2 : projectId,
                    TaskId: null,
                    ChannelId: channelEvent.ChannelId,
                    DeliveryMode: deliveryDecision.DeliveryMode,
                    Priority: 3,
                    Reason: channelEvent.EventType,
                    ContextSummary: message?.Body ?? $"Channel event {channelEvent.SourceId}",
                    ContextLink: $"den://channel/{channelEvent.ChannelId}/message/{channelEvent.Cursor}",
                    MetadataJson: JsonSerializer.Serialize(channelMetadata, JsonOptions),
                    Status: "pending",
                    SuppressionReason: null,
                    DedupeKey: dedupeKey,
                    CascadeDepth: cascadeDepth,
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
            await _database.UpsertDeliveryLoopCursorAsync("channels", cursorScopeKey, nextCursor, now, cancellationToken);
        }

        return new GatewayDeliveryPollResult("completed", seen, created, duplicates, suppressed, nextCursor, null, null);
    }

    private static string? GetChannelsCursorScopeKey(string? projectId, string? channelId)
    {
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            return $"channel:{channelId.Trim()}";
        }

        return string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
    }

    private static string GetMetadata(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DeliveryModeDecision ResolveMembershipDeliveryMode(ChannelMembershipSnapshot membership, ChannelMessageSnapshot? message, ChannelEventSnapshot channelEvent, string? directAgentTargetIdentity)
    {
        var wakePolicy = membership.WakePolicy.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(directAgentTargetIdentity)
            && string.Equals(membership.MemberIdentity, directAgentTargetIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return new DeliveryModeDecision("wake", false);
        }

        return wakePolicy switch
        {
            "wake" => new DeliveryModeDecision("wake", false),
            "notify" => new DeliveryModeDecision("notify", false),
            "record_only" => new DeliveryModeDecision(null, false),
            "never" => new DeliveryModeDecision(null, false),
            "all_human_messages" => IsHumanMessage(message)
                ? new DeliveryModeDecision("wake", false)
                : new DeliveryModeDecision(null, true),
            "all_messages_except_self" => message is not null && IsHumanMessage(message) && !IsSelfMessage(message, membership.MemberIdentity)
                ? new DeliveryModeDecision("wake", false)
                : new DeliveryModeDecision(null, true),
            "mentions_only" => MentionsMember(message, membership.MemberIdentity)
                ? new DeliveryModeDecision("wake", false)
                : new DeliveryModeDecision(null, true),
            "direct_questions_only" => IsDirectQuestion(message, membership.MemberIdentity)
                ? new DeliveryModeDecision("wake", false)
                : new DeliveryModeDecision(null, true),
            "substantive_digest" => IsHumanMessage(message)
                ? new DeliveryModeDecision("notify", false)
                : new DeliveryModeDecision(null, false),
            _ => new DeliveryModeDecision(null, false)
        };
    }

    private static bool IsHumanMessage(ChannelMessageSnapshot? message)
    {
        return message is not null
            && string.Equals(message.SenderType, "user", StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.MessageKind, "human_text", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetDirectAgentTargetIdentity(ChannelEventSnapshot channelEvent, ChannelMessageSnapshot? message)
    {
        // Use the channel event's SourceKind/SourceId as the authoritative
        // routing metadata. The event carries the wake- or direct-agent routing
        // decision, while the message content may have a different SourceKind
        // (e.g. "channel_message" for a human text payload posted into a
        // wake-event stream). Relying on message.SourceKind would miss the
        // direct-agent target when the message body lacks an @mention and the
        // target membership has a mentions_only wake policy.
        var sourceKind = channelEvent.SourceKind;
        var sourceId = channelEvent.SourceId;
        if (!string.Equals(sourceKind, "wake_event", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sourceKind, "direct_agent_message", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sourceId)
            || !sourceId.StartsWith("direct-agent-message:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = sourceId.Split(':');
        if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[2]))
        {
            return null;
        }

        // Legacy direct-agent source ids used the numeric channel_membership id
        // as the third segment. That is not enough to route safely from the
        // Gateway membership snapshot, so fail closed to normal wake-policy
        // evaluation rather than broadcasting a wake_event to every member.
        if (long.TryParse(parts[2], out _))
        {
            return null;
        }

        return Uri.UnescapeDataString(parts[2]);
    }

    private static int CalculateCascadeDepth(ChannelMessageSnapshot? message)
    {
        if (message is null)
        {
            return 0;
        }

        return string.Equals(message.SenderType, "agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.SourceKind, "gateway_delivery", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static bool IsSelfMessage(ChannelMessageSnapshot message, string memberIdentity)
    {
        return string.Equals(message.SenderIdentity, memberIdentity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detect agent-originated message without evidence of a human in the loop.
    /// When the sender is an agent (not user/human_text), this is conservatively
    /// treated as agent-tennis requiring a human reset.
    ///
    /// This method is intentionally a conservative proxy rather than tracking true
    /// message-chain state, because the full conversation history and human-in-loop
    /// status across multi-hop chains is not available at the Gateway delivery-loop
    /// layer. The proxy flags any agent-sent message as potentially part of an
    /// agent-tennis chain, then delegates to DeliveryPolicy to apply the configured
    /// brakes. Channels with the AgentTennisWithoutHumanResetEnabled override
    /// (e.g. agent-tennis-test) can relax this for controlled testing.
    /// </summary>
    private static bool IsAgentTennisWithoutHumanReset(ChannelMessageSnapshot? message)
    {
        if (message is null) return false;
        return string.Equals(message.SenderType, "agent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MentionsMember(ChannelMessageSnapshot? message, string memberIdentity)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.Body)) return false;
        var body = message.Body;
        return body.Contains($"@{memberIdentity}", StringComparison.OrdinalIgnoreCase)
            || body.Contains($"@{memberIdentity.Replace("_", "-")}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectQuestion(ChannelMessageSnapshot? message, string memberIdentity)
    {
        return MentionsMember(message, memberIdentity) && message!.Body.Contains('?', StringComparison.Ordinal);
    }

    private readonly record struct DeliveryModeDecision(string? DeliveryMode, bool CountAsSuppressed);

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
    [property: JsonPropertyName("now")] DateTimeOffset? Now = null,
    [property: JsonPropertyName("seed_cursor_at_latest_when_missing")] bool SeedCursorAtLatestWhenMissing = false,
    [property: JsonPropertyName("channel_id")] string? ChannelId = null,
    [property: JsonPropertyName("channelId")] string? CamelCaseChannelId = null)
{
    public string? GetChannelId() => string.IsNullOrWhiteSpace(ChannelId) ? CamelCaseChannelId : ChannelId;
}

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
