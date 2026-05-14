using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenGateway.Service.Clients;

public sealed class HttpDenCoreClient : IDenCoreClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public HttpDenCoreClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("api/gateway/readiness", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ServiceHealthResult.Unavailable("http", $"http_{(int)response.StatusCode}", "Den Core gateway readiness endpoint is unavailable.");
        }

        var dto = await response.Content.ReadFromJsonAsync<GatewayReadinessDto>(JsonOptions, cancellationToken);
        if (dto is null)
        {
            return ServiceHealthResult.Unavailable("http", "invalid_response", "Den Core gateway readiness endpoint returned an empty or invalid response.");
        }

        var available = dto.Status is not null
            && (string.Equals(dto.Status, "ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dto.Status, "degraded", StringComparison.OrdinalIgnoreCase));
        return available
            ? ServiceHealthResult.Available("http", $"{dto.Service ?? "den-core"} reported {dto.Status}.")
            : ServiceHealthResult.Unavailable("http", "not_ready", $"{dto.Service ?? "den-core"} reported {dto.Status ?? "unknown"}.");
    }

    public async Task<ClientListResult<GatewayBindingSnapshot>> ListActiveBindingsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("api/gateway/bindings?status=active%2Cdegraded", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ClientListResult<GatewayBindingSnapshot>.Unavailable($"http_{(int)response.StatusCode}", "Den Core gateway binding projection failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<GatewayBindingsDto>(JsonOptions, cancellationToken);
        if (dto is null)
        {
            return ClientListResult<GatewayBindingSnapshot>.Unavailable("invalid_response", "Den Core gateway binding projection returned an empty or invalid response.");
        }

        var bindingDtos = dto.Items ?? dto.Bindings ?? Array.Empty<GatewayBindingDto>();
        var bindings = bindingDtos.Where(binding => !string.IsNullOrWhiteSpace(binding.InstanceId)).Select(ToSnapshot).ToArray();
        return ClientListResult<GatewayBindingSnapshot>.Available(bindings);
    }

    public async Task<ClientValueResult<SourceSummary>> GetSourceSummaryAsync(string sourceKind, string sourceId, string? projectId, CancellationToken cancellationToken = default)
    {
        var path = $"api/source-summaries/{Uri.EscapeDataString(sourceKind)}/{Uri.EscapeDataString(sourceId)}";
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            path += $"?projectId={Uri.EscapeDataString(projectId)}";
        }

        var response = await _httpClient.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ClientValueResult<SourceSummary>.Unavailable("not_found", $"Source summary {sourceKind}/{sourceId} was not found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ClientValueResult<SourceSummary>.Unavailable($"http_{(int)response.StatusCode}", "Den Core source-summary lookup failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<SourceSummaryDto>(JsonOptions, cancellationToken);
        return dto is null
            ? ClientValueResult<SourceSummary>.Unavailable("invalid_response", "Den Core source-summary lookup returned an empty or invalid response.")
            : ClientValueResult<SourceSummary>.Available(new SourceSummary(
                SourceKind: dto.SourceKind,
                SourceId: dto.SourceId,
                SourceProjectId: dto.SourceProjectId,
                Title: dto.Title,
                Summary: dto.Summary,
                DeepLink: dto.DeepLink,
                OccurredAt: ParseDateTimeOffset(dto.OccurredAt ?? dto.CreatedAt ?? string.Empty),
                Actor: dto.Actor,
                Severity: dto.Severity,
                Metadata: FlattenMetadata(dto.Metadata)));
    }

    public async Task<ClientListResult<GatewayOutboxEvent>> ReadEventOutboxAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(after))
        {
            query.Add($"after={Uri.EscapeDataString(after)}");
        }

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            query.Add($"projectId={Uri.EscapeDataString(projectId)}");
        }

        query.Add($"limit={Math.Clamp(limit, 1, 200)}");
        var response = await _httpClient.GetAsync($"api/events/outbox?{string.Join('&', query)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ClientListResult<GatewayOutboxEvent>.Unavailable($"http_{(int)response.StatusCode}", "Den Core event outbox read failed.");
        }

        var dto = await response.Content.ReadFromJsonAsync<OutboxDto>(JsonOptions, cancellationToken);
        if (dto is null)
        {
            return ClientListResult<GatewayOutboxEvent>.Unavailable("invalid_response", "Den Core event outbox returned an empty or invalid response.");
        }

        return ClientListResult<GatewayOutboxEvent>.Available(dto.Items.Select(item => new GatewayOutboxEvent(
            Cursor: item.Cursor,
            EventId: string.IsNullOrWhiteSpace(item.EventId) ? item.Cursor : item.EventId,
            EventType: item.EventType,
            ProjectId: item.ProjectId ?? item.SourceProjectId,
            SourceKind: item.SourceKind,
            SourceId: item.SourceId,
            OccurredAt: ParseDateTimeOffset(item.OccurredAt),
            Actor: item.Actor,
            SummaryHint: item.SummaryHint ?? item.Summary ?? string.Empty,
            DeepLink: item.DeepLink,
            Severity: item.Severity,
            DedupeKey: item.DedupeKey)).ToArray());
    }

    public async Task<ClientOperationResult> PostGatewayReconciliationEventsAsync(IReadOnlyList<GatewayReconciliationEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var gatewayEvent in events)
        {
            var request = new GatewaySentinelEventRequest(
                SentinelId: "den-gateway",
                EventType: gatewayEvent.EventKind,
                State: gatewayEvent.EventKind,
                ProjectId: null,
                OutageId: null,
                Reason: null,
                ObservedAt: gatewayEvent.CreatedAt,
                Cursor: null,
                Metadata: new Dictionary<string, string>
                {
                    ["targetIdentity"] = gatewayEvent.TargetIdentity ?? string.Empty,
                    ["payloadJson"] = gatewayEvent.PayloadJson
                },
                DedupeKey: $"gateway-reconciliation:{gatewayEvent.EventKind}:{gatewayEvent.CreatedAt:O}:{gatewayEvent.TargetIdentity}");

            var response = await _httpClient.PostAsJsonAsync("api/gateway/sentinel/events", request, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ClientOperationResult.Unavailable($"http_{(int)response.StatusCode}", "Den Core sentinel reconciliation event post failed.");
            }
        }

        return ClientOperationResult.Completed("Den Core accepted Gateway reconciliation events.");
    }

    private static GatewayBindingSnapshot ToSnapshot(GatewayBindingDto dto)
    {
        var metadata = new Dictionary<string, string>(dto.Metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["transportKind"] = dto.TransportKind ?? string.Empty,
            ["sessionId"] = dto.SessionId ?? string.Empty,
            ["agentFamily"] = dto.AgentFamily ?? string.Empty
        };

        return new GatewayBindingSnapshot(
            AdapterKind: dto.TransportKind ?? "den_core_binding",
            AdapterInstanceId: dto.InstanceId,
            AgentIdentity: dto.AgentIdentity,
            UserIdentity: null,
            ProjectId: dto.ProjectId,
            Role: dto.Role,
            Status: dto.Status,
            LastSeenAt: ParseNullableDateTimeOffset(dto.LastHeartbeat ?? dto.CheckedInAt),
            ExpiresAt: null,
            Metadata: metadata);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static DateTimeOffset? ParseNullableDateTimeOffset(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ParseDateTimeOffset(value);
    }

    private static IReadOnlyDictionary<string, string> FlattenMetadata(JsonElement? metadata)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is { ValueKind: JsonValueKind.Object } element)
        {
            AddMetadataValues(element, values);
        }

        return values;
    }

    private static void AddMetadataValues(JsonElement element, Dictionary<string, string> values)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    values[property.Name] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    values[property.Name] = property.Value.ToString();
                    break;
                case JsonValueKind.Object:
                    AddMetadataValues(property.Value, values);
                    break;
            }
        }
    }

    private sealed record GatewayReadinessDto(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("service")] string? Service,
        [property: JsonPropertyName("checked_at")] string? CheckedAt,
        [property: JsonPropertyName("checks")] JsonElement? Checks);

    private sealed record GatewayBindingsDto(
        [property: JsonPropertyName("items")] IReadOnlyList<GatewayBindingDto>? Items,
        [property: JsonPropertyName("bindings")] IReadOnlyList<GatewayBindingDto>? Bindings);

    private sealed record GatewayBindingDto(
        [property: JsonPropertyName("instance_id")] string InstanceId,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("agent_identity")] string? AgentIdentity,
        [property: JsonPropertyName("agent_family")] string? AgentFamily,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("transport_kind")] string? TransportKind,
        [property: JsonPropertyName("session_id")] string? SessionId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("checked_in_at")] string? CheckedInAt,
        [property: JsonPropertyName("last_heartbeat")] string? LastHeartbeat,
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

    private sealed record SourceSummaryDto(
        [property: JsonPropertyName("source_kind")] string SourceKind,
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("source_project_id")] string? SourceProjectId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("deep_link")] string DeepLink,
        [property: JsonPropertyName("occurred_at")] string? OccurredAt,
        [property: JsonPropertyName("created_at")] string? CreatedAt,
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("metadata")] JsonElement? Metadata);

    private sealed record OutboxDto([property: JsonPropertyName("items")] IReadOnlyList<OutboxItemDto> Items);

    private sealed record OutboxItemDto(
        [property: JsonPropertyName("cursor")] string Cursor,
        [property: JsonPropertyName("event_id")] string? EventId,
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("source_project_id")] string? SourceProjectId,
        [property: JsonPropertyName("source_kind")] string SourceKind,
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("occurred_at")] string OccurredAt,
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("summary_hint")] string? SummaryHint,
        [property: JsonPropertyName("deep_link")] string? DeepLink,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("dedupe_key")] string DedupeKey);

    private sealed record GatewaySentinelEventRequest(
        [property: JsonPropertyName("sentinel_id")] string SentinelId,
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("outage_id")] string? OutageId,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt,
        [property: JsonPropertyName("cursor")] string? Cursor,
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string> Metadata,
        [property: JsonPropertyName("dedupe_key")] string DedupeKey);
}
