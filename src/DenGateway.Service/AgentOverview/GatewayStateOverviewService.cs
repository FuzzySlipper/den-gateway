using System.Globalization;
using DenGateway.Service.Persistence;
using Microsoft.Data.Sqlite;

namespace DenGateway.Service.AgentOverview;

public sealed class GatewayStateOverviewService
{
    private const int StuckPendingMinutes = 15;
    private const int StuckDeliveringMinutes = 30;
    private const int StuckDeliveredMinutes = 10;
    private const int StuckAcknowledgedMinutes = 30;

    private readonly GatewayDatabase _database;

    public GatewayStateOverviewService(GatewayDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<GatewayStateOverviewResponse> GetGatewayStateOverviewAsync(
        GatewayStateOverviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return await GetGatewayStateOverviewAsync(request, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<GatewayStateOverviewResponse> GetGatewayStateOverviewAsync(
        GatewayStateOverviewRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit, 1, 500);
        var includeTerminalMinutes = Math.Max(1, request.IncludeTerminalMinutes);
        var terminalCutoff = now.AddMinutes(-includeTerminalMinutes);

        await using var connection = new SqliteConnection($"Data Source={_database.DatabasePath}");
        await connection.OpenAsync(cancellationToken);

        var bindings = await QueryBindingsAsync(connection, request, cancellationToken);
        var deliveries = await QueryDeliveriesAsync(connection, request, terminalCutoff, limit, cancellationToken);

        var groupedBindings = bindings
            .GroupBy(b => (b.ProjectId, b.AgentIdentity, b.Role))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(IsFreshBinding).ThenByDescending(b => b.LastSeenAt).ToList());

        var bindingByProjectAgent = bindings
            .Where(b => b.AgentIdentity is not null)
            .GroupBy(b => (b.ProjectId, b.AgentIdentity))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(IsFreshBinding).ThenByDescending(b => b.LastSeenAt).First());

        var deliveryGroups = new Dictionary<(string? ProjectId, string? AgentIdentity, string? Role), List<DeliveryRow>>();
        foreach (var delivery in deliveries)
        {
            var agentIdentity = delivery.AgentIdentity;
            var role = delivery.Role;
            if (agentIdentity is not null && bindingByProjectAgent.TryGetValue((delivery.ProjectId, agentIdentity), out var matchingBinding))
            {
                role = matchingBinding.Role;
            }

            var key = (delivery.ProjectId, agentIdentity, role);
            if (!deliveryGroups.TryGetValue(key, out var group))
            {
                group = [];
                deliveryGroups[key] = group;
            }

            group.Add(delivery);
        }

        var allKeys = groupedBindings.Keys.Concat(deliveryGroups.Keys)
            .Distinct()
            .OrderBy(k => k.ProjectId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(k => k.AgentIdentity ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(k => k.Role ?? string.Empty, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var agents = new List<GatewayStateGroup>();
        var totalDeliveries = 0;
        foreach (var key in allKeys)
        {
            groupedBindings.TryGetValue(key, out var exactBindings);
            var fallbackBindings = exactBindings ?? [];
            if (fallbackBindings.Count == 0 && key.AgentIdentity is not null)
            {
                fallbackBindings = bindings
                    .Where(b => string.Equals(b.ProjectId, key.ProjectId, StringComparison.Ordinal) && string.Equals(b.AgentIdentity, key.AgentIdentity, StringComparison.Ordinal))
                    .OrderByDescending(IsFreshBinding)
                    .ThenByDescending(b => b.LastSeenAt)
                    .ToList();
            }

            var groupDeliveries = deliveryGroups.GetValueOrDefault(key, []);
            var currentDeliveries = groupDeliveries.Where(d => !IsTerminal(d.Status)).Select(d => BuildDeliveryOverview(d, now)).ToList();
            var recentDeliveries = groupDeliveries.Where(d => IsTerminal(d.Status)).Select(d => BuildDeliveryOverview(d, now)).ToList();
            totalDeliveries += groupDeliveries.Count;

            var flags = new List<string>();
            if (fallbackBindings.Count == 0 && groupDeliveries.Count > 0)
                flags.Add("missing_binding");
            if (fallbackBindings.Count > 0 && fallbackBindings.All(b => !IsFreshBinding(b)))
                flags.Add("stale_binding");

            var state = ClassifyGroup(fallbackBindings, groupDeliveries, now);
            var counts = ComputeDeliveryCounts(groupDeliveries, now, state);
            agents.Add(new GatewayStateGroup(
                AgentKey: BuildAgentKey(key.ProjectId, key.AgentIdentity, key.Role),
                ProjectId: key.ProjectId,
                AgentIdentity: key.AgentIdentity,
                Role: key.Role,
                BindingFreshness: BindingFreshness(fallbackBindings),
                AdapterInstances: fallbackBindings.Select(b => BuildBindingInfo(b, now)).ToList(),
                DeliveryCounts: counts,
                CurrentDeliveries: currentDeliveries,
                RecentDeliveries: recentDeliveries,
                Flags: flags));
        }

        var freshCount = bindings.Count(IsFreshBinding);
        var staleCount = bindings.Count - freshCount;
        var bindingHealthStatus = staleCount > 0 && freshCount > 0 ? "degraded" : "available";
        if (bindings.Count == 0)
            bindingHealthStatus = "available";
        if (bindings.Count > 0 && freshCount == 0)
            bindingHealthStatus = "degraded";

        return new GatewayStateOverviewResponse
        {
            GeneratedAt = now,
            Service = "den-gateway",
            BindingHealth = new GatewayBindingHealth(
                Status: bindingHealthStatus,
                TotalCount: bindings.Count,
                FreshCount: freshCount,
                StaleCount: staleCount,
                Reason: staleCount == 0 ? null : "one_or_more_bindings_stale_or_inactive"),
            Agents = agents,
            Warnings = [],
            Metadata = new GatewayStateOverviewMetadata(
                TotalGroups: agents.Count,
                TotalBindings: bindings.Count,
                TotalDeliveries: totalDeliveries,
                Limit: limit,
                IncludeTerminalMinutes: includeTerminalMinutes)
        };
    }

    private static GatewayAdapterInstanceInfo BuildBindingInfo(BindingRow binding, DateTimeOffset now)
    {
        var isFresh = IsFreshBinding(binding, now);
        return new GatewayAdapterInstanceInfo(
            AdapterKind: binding.AdapterKind,
            AdapterInstanceId: binding.AdapterInstanceId,
            Status: binding.Status,
            LastSeenAt: binding.LastSeenAt,
            ExpiresAt: binding.ExpiresAt,
            IsStale: !isFresh,
            StalenessReason: isFresh ? null : BindingStalenessReason(binding, now),
            Metadata: new Dictionary<string, string>())
        {
            Id = binding.Id,
            CreatedAt = binding.CreatedAt
        };
    }

    private static GatewayDeliveryOverview BuildDeliveryOverview(DeliveryRow delivery, DateTimeOffset now)
    {
        var flags = new List<string>();
        if (IsStuck(delivery, now))
            flags.Add("stuck");
        if (delivery.Status == "delivered")
            flags.Add("delivered_not_completed");

        return new GatewayDeliveryOverview(
            DeliveryRequestId: delivery.Id,
            Status: delivery.Status,
            DeliveryMode: delivery.DeliveryMode,
            TargetType: delivery.TargetType,
            TargetIdentity: delivery.TargetIdentity,
            ProjectId: delivery.ProjectId,
            TaskId: delivery.TaskId,
            ChannelId: delivery.ChannelId,
            SourceKind: delivery.SourceKind,
            SourceId: delivery.SourceId,
            SourceProjectId: delivery.SourceProjectId,
            ContextSummary: Truncate(delivery.ContextSummary, 240),
            ContextLink: delivery.ContextLink,
            AttemptCount: delivery.AttemptCount,
            LeaseExpiresAt: delivery.LeaseExpiresAt,
            NextAttemptAt: delivery.NextAttemptAt,
            ExpiresAt: delivery.ExpiresAt,
            CreatedAt: delivery.CreatedAt,
            UpdatedAt: delivery.UpdatedAt,
            LastAttempt: delivery.LastAttempt,
            Flags: flags);
    }

    private static DeliverySummaryCounts ComputeDeliveryCounts(List<DeliveryRow> deliveries, DateTimeOffset now, string state)
    {
        var pending = deliveries.Count(d => d.Status == "pending" && !IsStuck(d, now));
        var delivering = deliveries.Count(d => d.Status == "delivering" && !IsStuck(d, now));
        var deliveredNotCompleted = deliveries.Count(d => d.Status is "delivered" or "acknowledged");
        var completedRecent = deliveries.Count(d => d.Status == "completed");
        var failedRecent = deliveries.Count(d => d.Status is "failed" or "expired");
        var suppressedRecent = deliveries.Count(d => d.Status == "suppressed");
        var stuck = deliveries.Count(d => IsStuck(d, now));
        var total = pending + delivering + deliveredNotCompleted + completedRecent + failedRecent + suppressedRecent + stuck;
        return new DeliverySummaryCounts(state, pending, delivering, deliveredNotCompleted, completedRecent, failedRecent, suppressedRecent, stuck, total);
    }

    private static string ClassifyGroup(IReadOnlyList<BindingRow> bindings, IReadOnlyList<DeliveryRow> deliveries, DateTimeOffset now)
    {
        if (deliveries.Any(d => d.Status == "delivering" && !IsStuck(d, now)))
            return "working";
        if (deliveries.Any(d => IsStuck(d, now)))
            return "stuck";
        if (deliveries.Any(d => d.Status is "delivered" or "acknowledged"))
            return "delivered_waiting_completion";
        if (deliveries.Any(d => d.Status == "pending"))
            return "queued";
        if (deliveries.Any(d => d.Status is "failed" or "expired") && !deliveries.Any(d => d.Status == "completed"))
            return "failed";
        if (deliveries.Any(d => d.Status == "suppressed") && !deliveries.Any(d => d.Status == "completed"))
            return "suppressed";
        return "idle";
    }

    private static bool IsStuck(DeliveryRow delivery, DateTimeOffset now) => delivery.Status switch
    {
        "pending" => delivery.CreatedAt <= now.AddMinutes(-StuckPendingMinutes),
        "delivering" => (delivery.LeaseExpiresAt is not null && delivery.LeaseExpiresAt < now) || delivery.CreatedAt <= now.AddMinutes(-StuckDeliveringMinutes),
        "delivered" => delivery.UpdatedAt <= now.AddMinutes(-StuckDeliveredMinutes),
        "acknowledged" => delivery.UpdatedAt <= now.AddMinutes(-StuckAcknowledgedMinutes),
        _ => false
    };

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "expired" or "suppressed";

    private static bool IsFreshBinding(BindingRow binding) => IsFreshBinding(binding, DateTimeOffset.UtcNow);

    private static bool IsFreshBinding(BindingRow binding, DateTimeOffset now) =>
        binding.Status is "active" or "degraded" && (binding.ExpiresAt is null || binding.ExpiresAt > now);

    private static string BindingFreshness(IReadOnlyList<BindingRow> bindings)
    {
        if (bindings.Count == 0)
            return "unknown";
        return bindings.Any(IsFreshBinding) ? "fresh" : "stale";
    }

    private static string BindingStalenessReason(BindingRow binding, DateTimeOffset now)
    {
        if (binding.Status == "inactive")
            return "inactive";
        if (binding.ExpiresAt is not null && binding.ExpiresAt <= now)
            return "expired";
        return "unknown";
    }

    private static string BuildAgentKey(string? projectId, string? agentIdentity, string? role) =>
        $"{projectId ?? "_global"}:{agentIdentity ?? "_unknown"}:{role ?? "_unknown"}";

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "…";
    }

    private static async Task<List<BindingRow>> QueryBindingsAsync(SqliteConnection connection, GatewayStateOverviewRequest request, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, adapter_kind, adapter_instance_id, agent_identity, project_id, role,
                   status, last_seen_at, expires_at, created_at
            FROM gateway_adapter_bindings
            WHERE (COALESCE($project_id, '') = '' OR project_id = $project_id)
              AND (COALESCE($agent_identity, '') = '' OR agent_identity = $agent_identity)
              AND (COALESCE($role, '') = '' OR role = $role)
            ORDER BY adapter_kind, adapter_instance_id;
            """;
        command.Parameters.AddWithValue("$project_id", DbValue(request.ProjectId));
        command.Parameters.AddWithValue("$agent_identity", DbValue(request.AgentIdentity));
        command.Parameters.AddWithValue("$role", DbValue(request.Role));

        var rows = new List<BindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BindingRow(
                Id: reader.GetInt64(0),
                AdapterKind: reader.GetString(1),
                AdapterInstanceId: reader.GetString(2),
                AgentIdentity: reader.IsDBNull(3) ? null : reader.GetString(3),
                ProjectId: reader.IsDBNull(4) ? null : reader.GetString(4),
                Role: reader.IsDBNull(5) ? null : reader.GetString(5),
                Status: reader.GetString(6),
                LastSeenAt: ReadDateTimeOffset(reader, 7),
                ExpiresAt: ReadDateTimeOffset(reader, 8),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private static async Task<List<DeliveryRow>> QueryDeliveriesAsync(SqliteConnection connection, GatewayStateOverviewRequest request, DateTimeOffset terminalCutoff, int limit, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dr.id, dr.project_id, dr.target_type, dr.target_identity, dr.status,
                   dr.lease_expires_at, dr.created_at, dr.updated_at, dr.attempt_count,
                   dr.source_kind, dr.source_id, dr.source_project_id, dr.task_id, dr.channel_id,
                   dr.delivery_mode, dr.context_summary, dr.context_link, dr.next_attempt_at, dr.expires_at,
                   da.id, da.attempt_number, da.adapter_binding_id, da.status, da.ack_kind,
                   da.external_message_id, da.session_id, da.observed_at, da.error_code, da.error_message
            FROM delivery_requests dr
            LEFT JOIN delivery_attempts da ON da.id = (
                SELECT id FROM delivery_attempts
                WHERE delivery_request_id = dr.id
                ORDER BY attempt_number DESC, id DESC
                LIMIT 1
            )
            WHERE (COALESCE($project_id, '') = '' OR dr.project_id = $project_id)
              AND (
                    ($agent_filter = 1 AND dr.target_type = 'agent' AND dr.target_identity = $agent_identity)
                 OR ($role_filter = 1 AND dr.target_type = 'role' AND dr.target_identity = $role)
                 OR ($agent_filter = 0 AND $role_filter = 0)
              )
              AND (
                    dr.status IN ('pending', 'delivering', 'delivered', 'acknowledged')
                 OR (dr.status IN ('completed', 'failed', 'expired', 'suppressed') AND dr.updated_at >= $terminal_cutoff)
              )
            ORDER BY dr.project_id, dr.target_identity, dr.created_at DESC
            LIMIT $limit;
            """;
        var agentFilter = !string.IsNullOrWhiteSpace(request.AgentIdentity) ? 1 : 0;
        var roleFilter = !string.IsNullOrWhiteSpace(request.Role) ? 1 : 0;
        command.Parameters.AddWithValue("$project_id", DbValue(request.ProjectId));
        command.Parameters.AddWithValue("$agent_filter", agentFilter);
        command.Parameters.AddWithValue("$agent_identity", DbValue(request.AgentIdentity));
        command.Parameters.AddWithValue("$role_filter", roleFilter);
        command.Parameters.AddWithValue("$role", DbValue(request.Role));
        command.Parameters.AddWithValue("$terminal_cutoff", terminalCutoff.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<DeliveryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var targetType = reader.GetString(2);
            var targetIdentity = reader.GetString(3);
            rows.Add(new DeliveryRow(
                Id: reader.GetInt64(0),
                ProjectId: reader.IsDBNull(1) ? null : reader.GetString(1),
                TargetType: targetType,
                TargetIdentity: targetIdentity,
                AgentIdentity: string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase) ? targetIdentity : null,
                Role: string.Equals(targetType, "role", StringComparison.OrdinalIgnoreCase) ? targetIdentity : null,
                Status: reader.GetString(4),
                LeaseExpiresAt: ReadDateTimeOffset(reader, 5),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                AttemptCount: reader.GetInt32(8),
                SourceKind: reader.GetString(9),
                SourceId: reader.IsDBNull(10) ? null : reader.GetString(10),
                SourceProjectId: reader.IsDBNull(11) ? null : reader.GetString(11),
                TaskId: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                ChannelId: reader.IsDBNull(13) ? null : reader.GetString(13),
                DeliveryMode: reader.GetString(14),
                ContextSummary: reader.IsDBNull(15) ? null : reader.GetString(15),
                ContextLink: reader.IsDBNull(16) ? null : reader.GetString(16),
                NextAttemptAt: ReadDateTimeOffset(reader, 17),
                ExpiresAt: ReadDateTimeOffset(reader, 18),
                LastAttempt: reader.IsDBNull(19) ? null : new GatewayDeliveryAttemptOverview(
                    AttemptId: reader.GetInt64(19),
                    AttemptNumber: reader.GetInt32(20),
                    AdapterBindingId: reader.IsDBNull(21) ? null : reader.GetInt64(21),
                    Status: reader.GetString(22),
                    AckKind: reader.IsDBNull(23) ? null : reader.GetString(23),
                    ExternalMessageId: reader.IsDBNull(24) ? null : reader.GetString(24),
                    SessionId: reader.IsDBNull(25) ? null : reader.GetString(25),
                    ObservedAt: ReadDateTimeOffset(reader, 26),
                    ErrorCode: reader.IsDBNull(27) ? null : reader.GetString(27),
                    ErrorMessage: reader.IsDBNull(28) ? null : Truncate(reader.GetString(28), 240))));
        }

        return rows;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private sealed record BindingRow(long Id, string AdapterKind, string AdapterInstanceId, string? AgentIdentity, string? ProjectId, string? Role, string Status, DateTimeOffset? LastSeenAt, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt);

    private sealed record DeliveryRow(
        long Id,
        string? ProjectId,
        string TargetType,
        string TargetIdentity,
        string? AgentIdentity,
        string? Role,
        string Status,
        DateTimeOffset? LeaseExpiresAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int AttemptCount,
        string SourceKind,
        string? SourceId,
        string? SourceProjectId,
        long? TaskId,
        string? ChannelId,
        string DeliveryMode,
        string? ContextSummary,
        string? ContextLink,
        DateTimeOffset? NextAttemptAt,
        DateTimeOffset? ExpiresAt,
        GatewayDeliveryAttemptOverview? LastAttempt);
}
