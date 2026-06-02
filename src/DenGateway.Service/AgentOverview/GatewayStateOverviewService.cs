using System.Globalization;
using DenGateway.Service.Bindings;
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
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => IsFreshBinding(b, now)).ThenByDescending(b => b.LastSeenAt).ToList());

        var bindingByProjectAgent = bindings
            .Where(b => b.AgentIdentity is not null)
            .GroupBy(b => (b.ProjectId, b.AgentIdentity))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => IsFreshBinding(b, now)).ThenByDescending(b => b.LastSeenAt).First());

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
                    .OrderByDescending(b => IsFreshBinding(b, now))
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
            if (fallbackBindings.Count > 0 && fallbackBindings.All(b => !IsFreshBinding(b, now)))
                flags.Add("stale_binding");

            var state = ClassifyGroup(fallbackBindings, groupDeliveries, now);
            var counts = ComputeDeliveryCounts(groupDeliveries, now, state);

            // Build child-run states from adapter bindings and deliveries
            var childRuns = BuildChildRunStates(fallbackBindings, groupDeliveries, now);
            var childrenCount = childRuns.Count;
            if (childrenCount > 1)
                flags.Add("has_multiple_children");
            if (childRuns.Any(c => c.Flags.Contains("stale")))
                flags.Add("child_stale");
            if (childRuns.Any(c => c.Flags.Contains("crashed")))
                flags.Add("child_crashed");

            // Detect profile identity from first binding's adapter instance pattern
            var profileIdentity = fallbackBindings.Count > 0
                ? ChildRunBindingIdentity.TryParseProfileIdentity(fallbackBindings[0].AdapterInstanceId)
                : null;

            agents.Add(new GatewayStateGroup(
                AgentKey: BuildAgentKey(key.ProjectId, key.AgentIdentity, key.Role),
                ProjectId: key.ProjectId,
                AgentIdentity: key.AgentIdentity,
                Role: key.Role,
                ProfileIdentity: profileIdentity,
                BindingFreshness: BindingFreshness(fallbackBindings),
                AdapterInstances: fallbackBindings.Select(b => BuildBindingInfo(b, now)).ToList(),
                ChildRuns: childRuns,
                ChildrenCount: childrenCount,
                DeliveryCounts: counts,
                CurrentDeliveries: currentDeliveries,
                RecentDeliveries: recentDeliveries,
                Flags: flags));
        }

        var freshCount = bindings.Count(b => IsFreshBinding(b, now));
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
                TotalChildRuns: agents.Sum(a => a.ChildrenCount),
                ProfilesWithChildren: agents.Count(a => a.ChildrenCount > 0),
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
        if (delivery.AssignmentId is not null && IsStaleAssignment(delivery, now))
            flags.Add("stale_assignment");

        // Build target-work projection from delivery metadata
        var targetWork = BuildTargetWork(delivery);
        var runtimeControl = BuildRuntimeControl(delivery);

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
            Flags: flags,
            AssignmentId: delivery.AssignmentId,
            RunId: delivery.RunId,
            WorkerIdentity: delivery.WorkerIdentity,
            WorkerRole: delivery.WorkerRole,
            AssignmentPurpose: delivery.AssignmentPurpose,
            Waterfall: BuildWaterfall(delivery, now),
            TargetWork: targetWork,
            RuntimeControl: runtimeControl);
    }

    /// <summary>
    /// Build target-work attribution from delivery metadata fields.
    /// Uses explicit targetProjectId/targetTaskId from Channels targetWork
    /// when available, falling back to delivery project/task context.
    /// </summary>
    private static DeliveryTargetWork? BuildTargetWork(DeliveryRow delivery)
    {
        // Extract targetWork fields from metadata JSON if present
        string? targetProjectId = delivery.TargetWorkProjectId ?? delivery.ProjectId;
        string? targetTaskId = delivery.TargetWorkTaskId ?? delivery.TaskId?.ToString();
        string? assignmentId = delivery.AssignmentId;
        string? runId = delivery.RunId;
        string? role = delivery.TargetWorkRole ?? delivery.WorkerRole;
        string? profileIdentity = delivery.TargetWorkProfileIdentity;

        // Only emit targetWork when we have meaningful attribution
        if (string.IsNullOrWhiteSpace(targetProjectId)
            && string.IsNullOrWhiteSpace(targetTaskId)
            && string.IsNullOrWhiteSpace(assignmentId)
            && string.IsNullOrWhiteSpace(runId)
            && string.IsNullOrWhiteSpace(role)
            && string.IsNullOrWhiteSpace(profileIdentity))
        {
            return null;
        }

        return new DeliveryTargetWork(
            TargetProjectId: targetProjectId,
            TargetTaskId: targetTaskId,
            AssignmentId: assignmentId,
            RunId: runId,
            Role: role,
            ProfileIdentity: profileIdentity);
    }

    /// <summary>
    /// Build runtime/control identity from delivery pipeline metadata.
    /// Separate from target work attribution to prevent conflation.
    /// </summary>
    private static DeliveryRuntimeControl? BuildRuntimeControl(DeliveryRow delivery)
    {
        if (string.IsNullOrWhiteSpace(delivery.ChannelId)
            && delivery.LastAttempt?.SessionId is null
            && string.IsNullOrWhiteSpace(delivery.AgentInstanceId)
            && string.IsNullOrWhiteSpace(delivery.PoolMemberId))
        {
            return null;
        }

        return new DeliveryRuntimeControl(
            ChannelId: delivery.ChannelId,
            SessionId: delivery.LastAttempt?.SessionId,
            AdapterInstanceId: null,
            AgentInstanceId: delivery.AgentInstanceId,
            PoolMemberId: delivery.PoolMemberId);
    }

    /// <summary>
    /// Compute a delivery latency waterfall from available timestamp evidence.
    /// Phases without provider/Channels telemetry are explicitly labelled
    /// provider_timing_unavailable rather than blended into bridge or runtime spans.
    /// </summary>
    private static DeliveryWaterfall? BuildWaterfall(DeliveryRow delivery, DateTimeOffset now)
    {
        var status = delivery.Status;
        var createdAt = delivery.CreatedAt;

        // Suppressed deliveries have no claim/callback timeline.
        if (status == "suppressed")
        {
            return new DeliveryWaterfall(
                StatusLabel: "suppressed",
                CreatedAt: createdAt,
                ClaimedAt: null,
                FirstCallbackAt: null,
                CompletedAt: null,
                GatewaySpanMs: null,
                BridgeSpanMs: null,
                RuntimeSpanMs: null,
                CallbackPersistedSpanMs: null,
                ProviderTiming: null,
                SuppressionReason: delivery.SuppressionReason);
        }

        // Pending deliveries have never been claimed.
        if (status is "pending" or "delivering" or "delivered" or "acknowledged")
        {
            var claimedAt = delivery.ClaimedAt;
            if (claimedAt is null)
            {
                // Never claimed — distinguish "not claimed yet" from "gateway unavailable"
                // based on delivery age. A fresh delivery is simply waiting; an old one
                // suggests the Gateway/runtime may be unavailable.
                var ageMinutes = (now - createdAt).TotalMinutes;
                var (statusLabel, claimGuidance) = ageMinutes < StuckPendingMinutes
                    ? ("not_claimed_yet", "Delivery is queued and waiting for a claim. The target worker may not have polled yet.")
                    : ("gateway_unavailable_or_unclaimed", "Delivery has been waiting longer than expected. Check whether the target worker/binding is active and the Gateway delivery loop is running. Use run-id-scoped readback for authoritative state.");

                var readbackHint = delivery.RunId is not null
                    ? $"GET /api/worker-pool/assignments/by-run/{delivery.RunId}"
                    : null;

                return new DeliveryWaterfall(
                    StatusLabel: statusLabel,
                    CreatedAt: createdAt,
                    ClaimedAt: null,
                    FirstCallbackAt: null,
                    CompletedAt: null,
                    GatewaySpanMs: null,
                    BridgeSpanMs: null,
                    RuntimeSpanMs: null,
                    CallbackPersistedSpanMs: null,
                    ProviderTiming: null,
                    SuppressionReason: null,
                    ClaimGuidance: claimGuidance,
                    ReadbackHint: readbackHint);
            }

            var gatewaySpanMs = (claimedAt.Value - createdAt).TotalMilliseconds;
            var firstCallbackAt = delivery.LastAttempt?.ObservedAt;

            if (firstCallbackAt is null)
            {
                // Claimed but no callback yet — distinguish from "not claimed yet"
                var callbackGuidance = "Delivery was claimed by a bridge/adapter but no runtime callback has been received yet. The worker may be processing or the callback may be delayed.";
                var callbackReadbackHint = delivery.RunId is not null
                    ? $"GET /api/worker-pool/assignments/by-run/{delivery.RunId}"
                    : null;

                return new DeliveryWaterfall(
                    StatusLabel: "bridge_claimed_waiting_runtime",
                    CreatedAt: createdAt,
                    ClaimedAt: claimedAt,
                    FirstCallbackAt: null,
                    CompletedAt: null,
                    GatewaySpanMs: Math.Round(gatewaySpanMs, 1),
                    BridgeSpanMs: null,
                    RuntimeSpanMs: null,
                    CallbackPersistedSpanMs: null,
                    ProviderTiming: "provider_timing_unavailable",
                    SuppressionReason: null,
                    ClaimGuidance: callbackGuidance,
                    ReadbackHint: callbackReadbackHint);
            }

            var bridgeSpanMs = (firstCallbackAt.Value - claimedAt.Value).TotalMilliseconds;
            var completedAt = delivery.CompletedAt;

            if (completedAt is null)
            {
                // Have first callback but not yet complete
                return new DeliveryWaterfall(
                    StatusLabel: status switch
                    {
                        "delivering" => "delivering_with_first_reply",
                        "delivered" => "delivered_waiting_ack_or_complete",
                        "acknowledged" => "acknowledged_waiting_complete",
                        _ => "bridge_claimed_waiting_runtime"
                    },
                    CreatedAt: createdAt,
                    ClaimedAt: claimedAt,
                    FirstCallbackAt: firstCallbackAt,
                    CompletedAt: null,
                    GatewaySpanMs: Math.Round(gatewaySpanMs, 1),
                    BridgeSpanMs: Math.Round(bridgeSpanMs, 1),
                    RuntimeSpanMs: null,
                    CallbackPersistedSpanMs: null,
                    ProviderTiming: "provider_timing_unavailable",
                    SuppressionReason: null);
            }

            // Full timeline
            var runtimeSpanMs = (completedAt.Value - firstCallbackAt.Value).TotalMilliseconds;
            return new DeliveryWaterfall(
                StatusLabel: "callback_persisted",
                CreatedAt: createdAt,
                ClaimedAt: claimedAt,
                FirstCallbackAt: firstCallbackAt,
                CompletedAt: completedAt,
                GatewaySpanMs: Math.Round(gatewaySpanMs, 1),
                BridgeSpanMs: Math.Round(bridgeSpanMs, 1),
                RuntimeSpanMs: Math.Round(runtimeSpanMs, 1),
                CallbackPersistedSpanMs: null,
                ProviderTiming: "provider_timing_unavailable",
                SuppressionReason: null);
        }

        // Terminal states (completed, failed, expired)
        {
            var claimedAt = delivery.ClaimedAt;
            var completedAt = delivery.CompletedAt ?? delivery.UpdatedAt;
            var firstCallbackAt = delivery.LastAttempt?.ObservedAt;

            if (claimedAt is null && firstCallbackAt is null)
            {
                var terminalReadbackHint = delivery.RunId is not null
                    ? $"GET /api/worker-pool/assignments/by-run/{delivery.RunId}"
                    : null;
                return new DeliveryWaterfall(
                    StatusLabel: "terminal_unclaimed",
                    CreatedAt: createdAt,
                    ClaimedAt: null,
                    FirstCallbackAt: null,
                    CompletedAt: completedAt,
                    GatewaySpanMs: null,
                    BridgeSpanMs: null,
                    RuntimeSpanMs: null,
                    CallbackPersistedSpanMs: null,
                    ProviderTiming: null,
                    SuppressionReason: null,
                    ClaimGuidance: "Delivery reached a terminal state without being claimed. This may indicate the delivery expired before a worker polled, or the Gateway was unavailable for the entire delivery window.",
                    ReadbackHint: terminalReadbackHint);
            }

            if (claimedAt is not null && firstCallbackAt is null)
            {
                var gatewaySpanMs = (claimedAt.Value - createdAt).TotalMilliseconds;
                var runtimeSpanMs = (completedAt - claimedAt.Value).TotalMilliseconds;
                return new DeliveryWaterfall(
                    StatusLabel: "terminal_no_first_reply",
                    CreatedAt: createdAt,
                    ClaimedAt: claimedAt,
                    FirstCallbackAt: null,
                    CompletedAt: completedAt,
                    GatewaySpanMs: Math.Round(gatewaySpanMs, 1),
                    BridgeSpanMs: null,
                    RuntimeSpanMs: Math.Round(runtimeSpanMs, 1),
                    CallbackPersistedSpanMs: null,
                    ProviderTiming: "provider_timing_unavailable",
                    SuppressionReason: null);
            }

            if (claimedAt is null)
            {
                // Only have first callback but no claim recorded
                return new DeliveryWaterfall(
                    StatusLabel: status,
                    CreatedAt: createdAt,
                    ClaimedAt: null,
                    FirstCallbackAt: firstCallbackAt,
                    CompletedAt: completedAt,
                    GatewaySpanMs: null,
                    BridgeSpanMs: null,
                    RuntimeSpanMs: (firstCallbackAt is not null) ? Math.Round((completedAt - firstCallbackAt.Value).TotalMilliseconds, 1) : null,
                    CallbackPersistedSpanMs: null,
                    ProviderTiming: "provider_timing_unavailable",
                    SuppressionReason: null);
            }

            // Full timeline
            var gwMs = (claimedAt.Value - createdAt).TotalMilliseconds;
            var brMs = (firstCallbackAt!.Value - claimedAt.Value).TotalMilliseconds;
            var rtMs = (completedAt - firstCallbackAt.Value).TotalMilliseconds;
            return new DeliveryWaterfall(
                StatusLabel: status switch
                {
                    "completed" => "callback_persisted",
                    "failed" => "failed_after_callback",
                    "expired" => "expired_after_claim",
                    _ => "terminal"
                },
                CreatedAt: createdAt,
                ClaimedAt: claimedAt,
                FirstCallbackAt: firstCallbackAt,
                CompletedAt: completedAt,
                GatewaySpanMs: Math.Round(gwMs, 1),
                BridgeSpanMs: Math.Round(brMs, 1),
                RuntimeSpanMs: Math.Round(rtMs, 1),
                CallbackPersistedSpanMs: null,
                ProviderTiming: "provider_timing_unavailable",
                SuppressionReason: null);
        }
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

    private const int StaleAssignmentMinutes = 15;

    /// <summary>
    /// An assignment-delivery is considered stale when it has an assignment_id
    /// and the delivery has been in a non-terminal state for longer than
    /// StaleAssignmentMinutes without making progress. This indicates the
    /// leased worker may have lost its assignment or the Core assignment
    /// may have expired while the Gateway delivery is still active.
    /// </summary>
    private static bool IsStaleAssignment(DeliveryRow delivery, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(delivery.AssignmentId))
            return false;
        return delivery.Status is "pending" or "delivering" or "delivered" or "acknowledged"
            && delivery.CreatedAt <= now.AddMinutes(-StaleAssignmentMinutes);
    }

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
                   dr.suppression_reason,
                   dr.lease_expires_at, dr.created_at, dr.updated_at,
                   dr.claimed_at, dr.completed_at,
                   dr.attempt_count,
                   dr.source_kind, dr.source_id, dr.source_project_id, dr.task_id, dr.channel_id,
                   dr.delivery_mode, dr.context_summary, dr.context_link, dr.next_attempt_at, dr.expires_at,
                   dr.assignment_id, dr.worker_identity, dr.worker_role, dr.assignment_purpose,
                   dr.agent_instance_id, dr.pool_member_id,
                   COALESCE(
                       json_extract(dr.metadata_json, '$.summary_metadata.runId'),
                       json_extract(dr.metadata_json, '$.summary_metadata.workerRunId'),
                       json_extract(dr.metadata_json, '$.run_id'),
                       json_extract(dr.metadata_json, '$.workerRunId'),
                       json_extract(dr.metadata_json, '$.worker_run_id')) as run_id,
                   -- Target-work attribution from Channels targetWork fields
                   json_extract(dr.metadata_json, '$.target_work.targetProjectId') as target_work_project_id,
                   json_extract(dr.metadata_json, '$.target_work.targetTaskId') as target_work_task_id,
                   json_extract(dr.metadata_json, '$.target_work.role') as target_work_role,
                   json_extract(dr.metadata_json, '$.target_work.profileIdentity') as target_work_profile_identity,
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
                TargetType: reader.GetString(2),
                TargetIdentity: reader.GetString(3),
                AgentIdentity: string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase) ? targetIdentity : null,
                Role: string.Equals(targetType, "role", StringComparison.OrdinalIgnoreCase) ? targetIdentity : null,
                Status: reader.GetString(4),
                SuppressionReason: reader.IsDBNull(5) ? null : reader.GetString(5),
                LeaseExpiresAt: ReadDateTimeOffset(reader, 6),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                ClaimedAt: ReadDateTimeOffset(reader, 9),
                CompletedAt: ReadDateTimeOffset(reader, 10),
                AttemptCount: reader.GetInt32(11),
                SourceKind: reader.GetString(12),
                SourceId: reader.IsDBNull(13) ? null : reader.GetString(13),
                SourceProjectId: reader.IsDBNull(14) ? null : reader.GetString(14),
                TaskId: reader.IsDBNull(15) ? null : reader.GetInt64(15),
                ChannelId: reader.IsDBNull(16) ? null : reader.GetString(16),
                DeliveryMode: reader.GetString(17),
                ContextSummary: reader.IsDBNull(18) ? null : reader.GetString(18),
                ContextLink: reader.IsDBNull(19) ? null : reader.GetString(19),
                NextAttemptAt: ReadDateTimeOffset(reader, 20),
                ExpiresAt: ReadDateTimeOffset(reader, 21),
                AssignmentId: reader.IsDBNull(22) ? null : reader.GetString(22),
                WorkerIdentity: reader.IsDBNull(23) ? null : reader.GetString(23),
                WorkerRole: reader.IsDBNull(24) ? null : reader.GetString(24),
                AssignmentPurpose: reader.IsDBNull(25) ? null : reader.GetString(25),
                AgentInstanceId: reader.IsDBNull(26) ? null : reader.GetString(26),
                PoolMemberId: reader.IsDBNull(27) ? null : reader.GetString(27),
                RunId: reader.IsDBNull(28) ? null : reader.GetString(28),
                TargetWorkProjectId: reader.IsDBNull(29) ? null : reader.GetString(29),
                TargetWorkTaskId: reader.IsDBNull(30) ? null : reader.GetString(30),
                TargetWorkRole: reader.IsDBNull(31) ? null : reader.GetString(31),
                TargetWorkProfileIdentity: reader.IsDBNull(32) ? null : reader.GetString(32),
                LastAttempt: reader.IsDBNull(33) ? null : new GatewayDeliveryAttemptOverview(
                    AttemptId: reader.GetInt64(33),
                    AttemptNumber: reader.GetInt32(34),
                    AdapterBindingId: reader.IsDBNull(35) ? null : reader.GetInt64(35),
                    Status: reader.GetString(36),
                    AckKind: reader.IsDBNull(37) ? null : reader.GetString(37),
                    ExternalMessageId: reader.IsDBNull(38) ? null : reader.GetString(38),
                    SessionId: reader.IsDBNull(39) ? null : reader.GetString(39),
                    ObservedAt: ReadDateTimeOffset(reader, 40),
                    ErrorCode: reader.IsDBNull(41) ? null : reader.GetString(41),
                    ErrorMessage: reader.IsDBNull(42) ? null : Truncate(reader.GetString(42), 240))));
        }

        return rows;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static IReadOnlyList<ChildRunState> BuildChildRunStates(
        IReadOnlyList<BindingRow> bindings, IReadOnlyList<DeliveryRow> deliveries, DateTimeOffset now)
    {
        var results = new List<ChildRunState>();
        var seenAdapterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in bindings)
        {
            var adapterId = binding.AdapterInstanceId;
            if (!ChildRunBindingIdentity.IsChildRunBinding(adapterId))
                continue;
            if (!seenAdapterIds.Add(adapterId))
                continue;

            var bindingRunId = ChildRunBindingIdentity.TryParseRunId(adapterId);

            // Find deliveries associated with this adapter instance via concrete routing metadata.
            // Do not mark every child under the same supervisor busy just because one sibling has work.
            var childDeliveries = deliveries
                .Where(d => !IsTerminal(d.Status) && DeliveryMatchesChildBinding(d, binding, adapterId, bindingRunId))
                .ToList();

            var flags = new List<string>();
            var isFresh = IsFreshBinding(binding, now);

            // Derive status
            string status;
            if (!isFresh && childDeliveries.Any(d => !IsTerminal(d.Status)))
            {
                status = "crashed";
                flags.Add("crashed");
                flags.Add("binding_inactive_with_deliveries");
            }
            else if (!isFresh)
            {
                status = "stale";
                flags.Add("stale");
            }
            else if (childDeliveries.Any(d => d.Status == "delivering"))
            {
                status = "busy";
            }
            else if (childDeliveries.Any(d => d.Status is "delivered" or "acknowledged"))
            {
                status = "busy";
            }
            else if (childDeliveries.Any(d => d.Status == "pending"))
            {
                status = "busy";
            }
            else
            {
                status = "available";
            }

            // Find any active assignment
            var activeDelivery = childDeliveries.FirstOrDefault();
            var leaseId = activeDelivery?.AssignmentId is not null && activeDelivery.WorkerIdentity is not null
                ? $"{activeDelivery.WorkerIdentity}:{activeDelivery.RunId}"
                : null;

            // Build target-work projection for the child run
            var childTargetWork = BuildChildRunTargetWork(activeDelivery, binding);

            // Build runtime/control identity for the child run
            var childRuntimeControl = new ChildRunRuntimeControl(
                AdapterInstanceId: adapterId,
                AdapterKind: binding.AdapterKind,
                BindingStatus: binding.Status);

            results.Add(new ChildRunState(
                AdapterInstanceId: adapterId,
                AgentIdentity: binding.AgentIdentity,
                Role: binding.Role,
                Status: status,
                AssignmentId: activeDelivery?.AssignmentId,
                RunId: activeDelivery?.RunId,
                LeaseId: leaseId,
                LastSeenAt: binding.LastSeenAt,
                StaleAfterSeconds: null,
                Flags: flags,
                TargetWork: childTargetWork,
                RuntimeControl: childRuntimeControl));
        }

        return results;
    }

    /// <summary>
    /// Build target-work attribution for a child-run binding from its active delivery
    /// and binding metadata. Uses explicit targetWork fields from delivery when available,
    /// falls back to binding/delivery context.
    /// </summary>
    private static ChildRunTargetWork? BuildChildRunTargetWork(DeliveryRow? activeDelivery, BindingRow binding)
    {
        if (activeDelivery is null)
        {
            // No active delivery — use binding context only
            if (string.IsNullOrWhiteSpace(binding.ProjectId) && string.IsNullOrWhiteSpace(binding.Role))
                return null;

            return new ChildRunTargetWork(
                TargetProjectId: binding.ProjectId,
                TargetTaskId: null,
                AssignmentId: null,
                RunId: null,
                Role: binding.Role,
                ProfileIdentity: ChildRunBindingIdentity.TryParseProfileIdentity(binding.AdapterInstanceId));
        }

        string? targetProjectId = activeDelivery.TargetWorkProjectId ?? activeDelivery.ProjectId;
        string? targetTaskId = activeDelivery.TargetWorkTaskId ?? activeDelivery.TaskId?.ToString();
        string? role = activeDelivery.TargetWorkRole ?? activeDelivery.WorkerRole;
        string? profileIdentity = activeDelivery.TargetWorkProfileIdentity
            ?? ChildRunBindingIdentity.TryParseProfileIdentity(binding.AdapterInstanceId);

        return new ChildRunTargetWork(
            TargetProjectId: targetProjectId,
            TargetTaskId: targetTaskId,
            AssignmentId: activeDelivery.AssignmentId,
            RunId: activeDelivery.RunId,
            Role: role,
            ProfileIdentity: profileIdentity);
    }

    private static bool DeliveryMatchesChildBinding(DeliveryRow delivery, BindingRow binding, string adapterInstanceId, string? bindingRunId)
    {
        if (!string.IsNullOrWhiteSpace(delivery.AgentInstanceId)
            && string.Equals(delivery.AgentInstanceId, adapterInstanceId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(delivery.RunId)
            && !string.IsNullOrWhiteSpace(bindingRunId)
            && string.Equals(delivery.RunId, bindingRunId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private sealed record BindingRow(long Id, string AdapterKind, string AdapterInstanceId, string? AgentIdentity, string? ProjectId, string? Role, string Status, DateTimeOffset? LastSeenAt, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt);

    private sealed record DeliveryRow(
        long Id,
        string? ProjectId,
        string TargetType,
        string TargetIdentity,
        string? AgentIdentity,
        string? Role,
        string Status,
        string? SuppressionReason,
        DateTimeOffset? LeaseExpiresAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ClaimedAt,
        DateTimeOffset? CompletedAt,
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
        string? AssignmentId,
        string? WorkerIdentity,
        string? WorkerRole,
        string? AssignmentPurpose,
        string? AgentInstanceId,
        string? PoolMemberId,
        string? RunId,
        string? TargetWorkProjectId,
        string? TargetWorkTaskId,
        string? TargetWorkRole,
        string? TargetWorkProfileIdentity,
        GatewayDeliveryAttemptOverview? LastAttempt);
}
