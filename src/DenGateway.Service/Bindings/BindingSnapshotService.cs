using System.Text.Json;
using System.Text.Json.Serialization;
using DenGateway.Service.Clients;
using DenGateway.Service.Persistence;

namespace DenGateway.Service.Bindings;

public sealed class BindingSnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GatewayDatabase _database;
    private readonly IDenCoreClient _denCoreClient;
    private readonly BindingSnapshotSettings _settings;

    public BindingSnapshotService(GatewayDatabase database, IDenCoreClient denCoreClient, BindingSnapshotSettings settings)
    {
        _database = database;
        _denCoreClient = denCoreClient;
        _settings = settings;
    }

    public async Task<BindingSnapshotRefreshResult> RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var result = await _denCoreClient.ListActiveBindingsAsync(cancellationToken);
        if (!result.IsAvailable)
        {
            await RecordVisibleHealthTransitionAsync("degraded", result.ErrorCode ?? "core_bindings_unavailable", now, cancellationToken);
            return new BindingSnapshotRefreshResult("degraded", 0, result.ErrorCode, result.Message);
        }

        var writes = result.Items.Select(binding => new BindingSnapshotWrite(
            AdapterKind: binding.AdapterKind,
            AdapterInstanceId: binding.AdapterInstanceId,
            AgentIdentity: binding.AgentIdentity,
            ProjectId: binding.ProjectId,
            Role: binding.Role,
            Status: binding.Status,
            TransportEndpoint: binding.Metadata.TryGetValue("transportEndpoint", out var endpoint) ? endpoint : null,
            LastSeenAt: binding.LastSeenAt,
            ExpiresAt: binding.ExpiresAt,
            MetadataJson: JsonSerializer.Serialize(binding.Metadata, JsonOptions))).ToArray();
        await _database.UpsertBindingSnapshotsAsync(writes, now, cancellationToken);

        var health = await GetHealthAsync(now, cancellationToken);
        if (health.Status is "degraded")
        {
            await RecordVisibleHealthTransitionAsync("degraded", "binding_stale", now, cancellationToken);
        }
        else if (health.Status is "available")
        {
            await RecordVisibleHealthTransitionAsync("recovered", "bindings_fresh", now, cancellationToken);
        }

        return new BindingSnapshotRefreshResult("completed", writes.Length, null, null);
    }

    public async Task<IReadOnlyList<BindingSnapshotDto>> ListAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var rows = await _database.ListLatestBindingSnapshotsAsync(cancellationToken);
        return rows.Select(row => ToDto(row, now)).ToArray();
    }

    public async Task<BindingSnapshotHealth> GetHealthAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var snapshots = await ListAsync(now, cancellationToken);
        if (snapshots.Count == 0)
        {
            return new BindingSnapshotHealth("unknown", 0, 0, 0, "no_binding_snapshot");
        }

        var stale = snapshots.Count(snapshot => snapshot.IsStale);
        var fresh = snapshots.Count - stale;
        return stale > 0
            ? new BindingSnapshotHealth("degraded", snapshots.Count, fresh, stale, "stale_bindings")
            : new BindingSnapshotHealth("available", snapshots.Count, fresh, stale, null);
    }

    public async Task<bool> RecordVisibleHealthTransitionAsync(string transition, string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var eventKind = transition switch
        {
            "degraded" => "binding_health_degraded",
            "recovered" => "binding_health_recovered",
            _ => $"binding_health_{transition}"
        };
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["transition"] = transition,
            ["reason"] = reason,
            ["observed_at"] = now
        }, JsonOptions);
        var inserted = await _database.InsertSentinelEventIfChangedAsync(eventKind, null, payload, now, cancellationToken);
        if (inserted)
        {
            await _denCoreClient.PostGatewayReconciliationEventsAsync([
                new GatewayReconciliationEvent(eventKind, null, payload, now)
            ], cancellationToken);
        }

        return inserted;
    }

    private BindingSnapshotDto ToDto(BindingSnapshotRead row, DateTimeOffset now)
    {
        var staleReason = StalenessReason(row, now);
        return new BindingSnapshotDto(
            CapturedAt: row.CapturedAt,
            AgentIdentity: row.AgentIdentity,
            ProjectId: row.ProjectId,
            Role: row.Role,
            AdapterKind: row.AdapterKind,
            AdapterInstanceId: row.AdapterInstanceId,
            Status: row.Status,
            LastSeenAt: row.LastSeenAt,
            ExpiresAt: row.ExpiresAt,
            IsStale: staleReason is not null,
            StalenessReason: staleReason,
            MetadataJson: row.MetadataJson);
    }

    private string? StalenessReason(BindingSnapshotRead row, DateTimeOffset now)
    {
        if (row.ExpiresAt is not null && row.ExpiresAt <= now)
        {
            return "expired";
        }

        if (row.LastSeenAt is not null && row.LastSeenAt.Value.AddMinutes(_settings.BindingTtlMinutes) <= now)
        {
            return ChildRunBindingIdentity.IsChildRunBinding(row.AdapterInstanceId) ? "child_run_ttl_expired" : "ttl_expired";
        }

        if (!string.Equals(row.Status, "active", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.Status, "degraded", StringComparison.OrdinalIgnoreCase))
        {
            return ChildRunBindingIdentity.IsChildRunBinding(row.AdapterInstanceId) ? "child_run_inactive" : "inactive";
        }

        return null;
    }
}

public sealed record BindingSnapshotSettings(int BindingTtlMinutes);

public sealed record BindingSnapshotRefreshRequest([property: JsonPropertyName("now")] DateTimeOffset? Now = null);
public sealed record BindingSnapshotRefreshResult(string Status, int RefreshedCount, string? ErrorCode, string? Message);
public sealed record BindingSnapshotListResponse(IReadOnlyList<BindingSnapshotDto> Items, BindingSnapshotHealth Health);
public sealed record BindingSnapshotHealth(string Status, int TotalCount, int FreshCount, int StaleCount, string? Reason);
public sealed record BindingSnapshotDto(DateTimeOffset CapturedAt, string? AgentIdentity, string? ProjectId, string? Role, string AdapterKind, string AdapterInstanceId, string Status, DateTimeOffset? LastSeenAt, DateTimeOffset? ExpiresAt, bool IsStale, string? StalenessReason, string MetadataJson);
