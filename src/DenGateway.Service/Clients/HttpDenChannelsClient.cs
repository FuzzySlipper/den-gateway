using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DenGateway.Service.Clients;

public sealed class HttpDenChannelsClient : IDenChannelsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public HttpDenChannelsClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("/api/gateway/health", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ServiceHealthResult.Unavailable("http", $"http_{(int)response.StatusCode}", "Den Channels gateway health endpoint is unavailable.");
        }

        var dto = await response.Content.ReadFromJsonAsync<GatewayHealthDto>(JsonOptions, cancellationToken);
        if (dto is null)
        {
            return ServiceHealthResult.Unavailable("http", "invalid_response", "Den Channels gateway health endpoint returned an empty or invalid response.");
        }

        var available = string.Equals(dto.Status, "ready", StringComparison.OrdinalIgnoreCase);
        return available
            ? ServiceHealthResult.Available("http", $"{dto.Service} reported {dto.Status}.")
            : ServiceHealthResult.Unavailable("http", "not_ready", $"{dto.Service} reported {dto.Status}.");
    }

    public async Task<ClientValueResult<ChannelMembershipListSnapshot>> ListProjectMembershipsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/api/gateway/memberships?projectId={Uri.EscapeDataString(projectId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ClientValueResult<ChannelMembershipListSnapshot>.Unavailable("not_found", $"Project {projectId} has no default Den Channels membership surface.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ClientValueResult<ChannelMembershipListSnapshot>.Unavailable($"http_{(int)response.StatusCode}", "Den Channels project membership lookup failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<GatewayMembershipsDto>(JsonOptions, cancellationToken);
        if (dto is null)
        {
            return ClientValueResult<ChannelMembershipListSnapshot>.Unavailable("invalid_response", "Den Channels project membership lookup returned an empty or invalid response.");
        }

        var members = dto.Members.Select(member => new ChannelMembershipSnapshot(
            ChannelId: dto.ChannelId.ToString(),
            MemberType: member.MemberType,
            MemberIdentity: member.MemberIdentity,
            WakePolicy: member.WakePolicy,
            Status: member.MembershipStatus,
            CooldownSeconds: member.CooldownSeconds,
            Settings: ToSettings(dto, member))).ToArray();

        return ClientValueResult<ChannelMembershipListSnapshot>.Available(new ChannelMembershipListSnapshot(
            ChannelId: dto.ChannelId.ToString(),
            ChannelSlug: dto.ChannelSlug,
            ChannelKind: dto.ChannelKind,
            ProjectId: dto.ProjectId,
            Members: members));
    }

    public async Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/api/gateway/messages/{Uri.EscapeDataString(channelMessageId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ClientValueResult<ChannelMessageSnapshot>.Unavailable("not_found", $"Channel message {channelMessageId} was not found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ClientValueResult<ChannelMessageSnapshot>.Unavailable($"http_{(int)response.StatusCode}", "Den Channels message lookup failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<GatewayMessageDto>(JsonOptions, cancellationToken);
        return dto is null
            ? ClientValueResult<ChannelMessageSnapshot>.Unavailable("invalid_response", "Den Channels message lookup returned an empty or invalid response.")
            : ClientValueResult<ChannelMessageSnapshot>.Available(ToSnapshot(dto));
    }

    public async Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/api/gateway/memberships?channelId={Uri.EscapeDataString(channelId)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ClientListResult<ChannelMembershipSnapshot>.Unavailable($"http_{(int)response.StatusCode}", "Den Channels membership lookup failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<GatewayMembershipsDto>(JsonOptions, cancellationToken);
        if (dto is null)
        {
            return ClientListResult<ChannelMembershipSnapshot>.Unavailable("invalid_response", "Den Channels membership lookup returned an empty or invalid response.");
        }

        var items = dto.Members.Select(member => new ChannelMembershipSnapshot(
            ChannelId: dto.ChannelId.ToString(),
            MemberType: member.MemberType,
            MemberIdentity: member.MemberIdentity,
            WakePolicy: member.WakePolicy,
            Status: member.MembershipStatus,
            CooldownSeconds: member.CooldownSeconds,
            Settings: ToSettings(dto, member))).ToArray();

        return ClientListResult<ChannelMembershipSnapshot>.Available(items);
    }

    public async Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default)
    {
        // Channels keeps /api/gateway/system-messages as the compatibility route name.
        // In communication-surface terms this posts a Gateway-generated channel_message;
        // gateway_delivery final replies must use this path only for the terminal visible
        // reply, while interim progress goes through channel activity events.
        var request = new PostGatewaySystemMessageRequest(
            ChannelId: long.TryParse(message.ChannelId, out var channelId) ? channelId : null,
            ProjectId: long.TryParse(message.ChannelId, out _) ? null : message.ChannelId,
            SenderIdentity: "den-gateway",
            MessageKind: message.MessageKind,
            Body: message.Body,
            SourceKind: message.SourceKind,
            SourceId: message.SourceId,
            SourceProjectId: message.Metadata.TryGetValue("sourceProjectId", out var sourceProjectId) ? sourceProjectId : null,
            Summary: message.Metadata.TryGetValue("summary", out var summary) ? summary : null,
            DeepLink: message.DeepLink,
            MetadataJson: JsonSerializer.Serialize(message.Metadata, JsonOptions),
            DedupeKey: message.DedupeKey);

        var response = await _httpClient.PostAsJsonAsync("/api/gateway/system-messages", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ClientOperationResult.Unavailable($"http_{(int)response.StatusCode}", "Den Channels Gateway-generated channel-message post failed.");
        }

        return ClientOperationResult.Completed("Den Channels accepted the Gateway-generated channel message.");
    }

    public async Task<ChannelActivityPostResult> PostActivityEventAsync(ChannelActivityEventWrite activityEvent,
        CancellationToken cancellationToken = default)
    {
        var request = new PostChannelActivityEventRequest(
            ProjectId: activityEvent.ProjectId,
            AgentIdentity: activityEvent.AgentIdentity,
            DeliveryRequestId: activityEvent.DeliveryRequestId,
            DisplayBlockId: activityEvent.DisplayBlockId,
            HermesSessionKey: activityEvent.HermesSessionKey,
            ParentHermesSessionKey: activityEvent.ParentHermesSessionKey,
            ParentAgentIdentity: activityEvent.ParentAgentIdentity,
            WorkerRunId: activityEvent.WorkerRunId,
            WorkerRole: activityEvent.WorkerRole,
            TaskId: activityEvent.TaskId,
            ThreadId: activityEvent.ThreadId,
            AnchorMessageId: activityEvent.AnchorMessageId,
            EventType: activityEvent.EventType,
            Status: activityEvent.Status,
            Sequence: activityEvent.Sequence,
            Title: activityEvent.Title,
            Summary: activityEvent.Summary,
            PreviewJson: activityEvent.PreviewJson,
            MetadataJson: activityEvent.MetadataJson,
            DedupeKey: activityEvent.DedupeKey);
        var response = await _httpClient.PostAsJsonAsync($"/api/channels/{Uri.EscapeDataString(activityEvent.ChannelId)}/activity-events", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ChannelActivityPostResult.Unavailable($"http_{(int)response.StatusCode}", "Den Channels activity-event post failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<ChannelActivityEventDto>(JsonOptions, cancellationToken);
        return ChannelActivityPostResult.Completed(dto?.Id.ToString(), "Den Channels accepted the activity event.");
    }

    public async Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, string? channelId, int limit, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            query.Add($"channelId={Uri.EscapeDataString(channelId)}");
        }
        else if (!string.IsNullOrWhiteSpace(projectId))
        {
            query.Add($"projectId={Uri.EscapeDataString(projectId)}");
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            query.Add($"afterId={Uri.EscapeDataString(after)}");
        }

        query.Add($"limit={limit}");
        var response = await _httpClient.GetAsync($"/api/gateway/events?{string.Join('&', query)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ClientListResult<ChannelEventSnapshot>.Unavailable($"http_{(int)response.StatusCode}", "Den Channels event cursor read failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<GatewayEventsDto>(JsonOptions, cancellationToken);
        if (dto is null)
        {
            return ClientListResult<ChannelEventSnapshot>.Unavailable("invalid_response", "Den Channels event cursor returned an empty or invalid response.");
        }

        var items = dto.Items.Select(ToSnapshot).ToArray();

        return ClientListResult<ChannelEventSnapshot>.Available(items);
    }

    public async Task<ClientValueResult<string>> GetLatestChannelEventCursorAsync(string projectId, CancellationToken cancellationToken = default)
    {
        const int pageSize = 200;
        long? afterId = 0;
        string? latest = null;
        while (true)
        {
            var query = $"projectId={Uri.EscapeDataString(projectId)}&afterId={afterId}&limit={pageSize}";
            var response = await _httpClient.GetAsync($"/api/gateway/events?{query}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ClientValueResult<string>.Unavailable($"http_{(int)response.StatusCode}", "Den Channels latest event cursor read failed.");
            }

            var dto = await response.Content.ReadFromJsonAsync<GatewayEventsDto>(JsonOptions, cancellationToken);
            if (dto is null)
            {
                return ClientValueResult<string>.Unavailable("invalid_response", "Den Channels latest event cursor returned an empty or invalid response.");
            }

            if (dto.Items.Count == 0)
            {
                return latest is null
                    ? ClientValueResult<string>.Unavailable("empty_cursor", $"Project {projectId} has no channel events to seed from.")
                    : ClientValueResult<string>.Available(latest);
            }

            latest = dto.Items[^1].Id.ToString();
            if (!dto.HasMore || dto.NextAfterId is null)
            {
                return ClientValueResult<string>.Available(latest);
            }

            afterId = dto.NextAfterId;
        }
    }

    private static ChannelEventSnapshot ToSnapshot(GatewayEventItemDto item)
    {
        var targetWork = item.TargetWork;
        return new ChannelEventSnapshot(
            Cursor: item.Id.ToString(),
            EventType: item.MessageKind,
            ChannelId: item.ChannelId.ToString(),
            SourceKind: item.SourceKind ?? "channel_message",
            SourceId: item.SourceId ?? item.Id.ToString(),
            DedupeKey: item.DedupeKey ?? $"channel-message:{item.Id}",
            OccurredAt: ParseDateTimeOffset(item.CreatedAt),
            TargetProjectId: targetWork?.TargetProjectId ?? JsonScalar(item.TargetProjectId),
            TargetTaskId: targetWork?.TargetTaskId ?? JsonScalar(item.TargetTaskId),
            AssignmentId: targetWork?.AssignmentId ?? JsonScalar(item.AssignmentId),
            RunId: targetWork?.RunId ?? JsonScalar(item.RunId),
            Role: targetWork?.Role ?? JsonScalar(item.Role),
            ProfileIdentity: targetWork?.ProfileIdentity ?? JsonScalar(item.ProfileIdentity),
            WorkerRunId: targetWork?.WorkerRunId ?? JsonScalar(item.WorkerRunId),
            WorkerRole: targetWork?.WorkerRole ?? JsonScalar(item.WorkerRole),
            AgentInstanceId: targetWork?.AgentInstanceId ?? JsonScalar(item.AgentInstanceId),
            PoolMemberId: targetWork?.PoolMemberId ?? JsonScalar(item.PoolMemberId));
    }

    private static IReadOnlyDictionary<string, string> ToSettings(GatewayMembershipsDto dto, GatewayMemberDto member)
    {
        var settings = new Dictionary<string, string>
        {
            ["channelSlug"] = dto.ChannelSlug,
            ["channelKind"] = dto.ChannelKind,
            ["projectId"] = dto.ProjectId ?? string.Empty,
            ["canSend"] = member.CanSend.ToString(),
            ["maxAutoRepliesPerWindow"] = member.MaxAutoRepliesPerWindow.ToString()
        };

        if (!string.IsNullOrWhiteSpace(member.SettingsLabel))
        {
            settings["settingsLabel"] = member.SettingsLabel;
        }

        return settings;
    }

    private static ChannelMessageSnapshot ToSnapshot(GatewayMessageDto dto) => new(
        ChannelMessageId: dto.Id.ToString(),
        ChannelId: dto.ChannelId.ToString(),
        SenderType: dto.SenderType,
        SenderIdentity: dto.SenderIdentity,
        MessageKind: dto.MessageKind,
        Body: dto.Body,
        SourceKind: dto.SourceKind,
        SourceId: dto.SourceId,
        DedupeKey: dto.DedupeKey,
        CreatedAt: ParseDateTimeOffset(dto.CreatedAt));

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static string? JsonScalar(JsonElement? value)
    {
        if (value is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => null
        };
    }

    private sealed record GatewayHealthDto(string Service, string Status, string[] Endpoints);
    private sealed record GatewayMembershipsDto(long ChannelId, string ChannelSlug, string ChannelKind, string? ProjectId, IReadOnlyList<GatewayMemberDto> Members);
    private sealed record GatewayMemberDto(long Id, string MemberType, string MemberIdentity, string MembershipStatus, string WakePolicy, bool CanSend, int CooldownSeconds, int MaxAutoRepliesPerWindow, string? SettingsLabel);
    private sealed record GatewayMessageDto(long Id, long ChannelId, string MessageKind, string SenderType, string SenderIdentity, string? SourceKind, string? SourceId, string? SourceProjectId, string? DedupeKey, string? DeepLink, string? Summary, string Body, string CreatedAt);
    private sealed record GatewayEventsDto(IReadOnlyList<GatewayEventItemDto> Items, long? NextAfterId, bool HasMore);
    private sealed record GatewayEventItemDto(
        long Id,
        long ChannelId,
        string MessageKind,
        string SenderType,
        string SenderIdentity,
        string? SourceKind,
        string? SourceId,
        string? SourceProjectId,
        string? DedupeKey,
        string? DeepLink,
        string? Summary,
        string Body,
        string CreatedAt,
        GatewayEventTargetWorkDto? TargetWork = null,
        JsonElement? TargetProjectId = null,
        JsonElement? TargetTaskId = null,
        JsonElement? AssignmentId = null,
        JsonElement? RunId = null,
        JsonElement? Role = null,
        JsonElement? ProfileIdentity = null,
        JsonElement? WorkerRunId = null,
        JsonElement? WorkerRole = null,
        JsonElement? AgentInstanceId = null,
        JsonElement? PoolMemberId = null);
    private sealed record GatewayEventTargetWorkDto(string? TargetProjectId, string? TargetTaskId, string? AssignmentId, string? RunId, string? Role, string? ProfileIdentity, string? WorkerRunId = null, string? WorkerRole = null, string? AgentInstanceId = null, string? PoolMemberId = null);
    private sealed record PostGatewaySystemMessageRequest(long? ChannelId, string? ProjectId, string SenderIdentity, string MessageKind, string Body, string SourceKind, string SourceId, string? SourceProjectId, string? Summary, string? DeepLink, string MetadataJson, string DedupeKey);
    private sealed record PostChannelActivityEventRequest(string? ProjectId, string AgentIdentity, string? DeliveryRequestId, string? DisplayBlockId, string? HermesSessionKey, string? ParentHermesSessionKey, string? ParentAgentIdentity, string? WorkerRunId, string? WorkerRole, long? TaskId, long? ThreadId, long? AnchorMessageId, string EventType, string Status, long? Sequence, string? Title, string? Summary, string? PreviewJson, string? MetadataJson, string? DedupeKey);
    private sealed record ChannelActivityEventDto(long Id);
}
