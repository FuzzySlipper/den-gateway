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
            return ClientOperationResult.Unavailable($"http_{(int)response.StatusCode}", "Den Channels system-message post failed.");
        }

        return ClientOperationResult.Completed("Den Channels accepted the Gateway system/mirror message.");
    }

    public async Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectId))
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

        var items = dto.Items.Select(item => new ChannelEventSnapshot(
            Cursor: item.Id.ToString(),
            EventType: item.MessageKind,
            ChannelId: item.ChannelId.ToString(),
            SourceKind: item.SourceKind ?? "channel_message",
            SourceId: item.SourceId ?? item.Id.ToString(),
            DedupeKey: item.DedupeKey ?? $"channel-message:{item.Id}",
            OccurredAt: ParseDateTimeOffset(item.CreatedAt))).ToArray();

        return ClientListResult<ChannelEventSnapshot>.Available(items);
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

    private sealed record GatewayHealthDto(string Service, string Status, string[] Endpoints);
    private sealed record GatewayMembershipsDto(long ChannelId, string ChannelSlug, string ChannelKind, string? ProjectId, IReadOnlyList<GatewayMemberDto> Members);
    private sealed record GatewayMemberDto(long Id, string MemberType, string MemberIdentity, string MembershipStatus, string WakePolicy, bool CanSend, int CooldownSeconds, int MaxAutoRepliesPerWindow, string? SettingsLabel);
    private sealed record GatewayMessageDto(long Id, long ChannelId, string MessageKind, string SenderType, string SenderIdentity, string? SourceKind, string? SourceId, string? SourceProjectId, string? DedupeKey, string? DeepLink, string? Summary, string Body, string CreatedAt);
    private sealed record GatewayEventsDto(IReadOnlyList<GatewayEventItemDto> Items, long? NextAfterId, bool HasMore);
    private sealed record GatewayEventItemDto(long Id, long ChannelId, string MessageKind, string SenderType, string SenderIdentity, string? SourceKind, string? SourceId, string? SourceProjectId, string? DedupeKey, string? DeepLink, string? Summary, string Body, string CreatedAt);
    private sealed record PostGatewaySystemMessageRequest(long? ChannelId, string? ProjectId, string SenderIdentity, string MessageKind, string Body, string SourceKind, string SourceId, string? SourceProjectId, string? Summary, string? DeepLink, string MetadataJson, string DedupeKey);
}
