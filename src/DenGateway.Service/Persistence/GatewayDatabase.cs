using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace DenGateway.Service.Persistence;

public sealed class GatewayDatabase
{
    private readonly string _databasePath;

    public GatewayDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        _databasePath = databasePath;
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);

        foreach (var statement in SchemaStatements)
        {
            await ExecuteNonQueryAsync(connection, statement, cancellationToken);
        }

        await EnsureColumnAsync(connection, "delivery_requests", "lease_expires_at", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_requests", "metadata_json", "TEXT NOT NULL DEFAULT '{}'", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_requests", "assignment_id", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_requests", "worker_identity", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_requests", "worker_role", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_requests", "assignment_purpose", "TEXT NULL", cancellationToken);
        // Delivery latency waterfall columns
        await EnsureColumnAsync(connection, "delivery_requests", "claimed_at", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_requests", "completed_at", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_attempts", "ack_kind", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_attempts", "external_message_id", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_attempts", "session_id", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "delivery_attempts", "observed_at", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "discord_notifications", "dedupe_key", "TEXT NOT NULL UNIQUE", cancellationToken);
        await EnsureColumnAsync(connection, "discord_notification_attempts", "notification_id", "INTEGER NOT NULL", cancellationToken);
        await SeedSentinelStateAsync(connection, cancellationToken);
    }

    public async Task<long> UpsertAdapterBindingHeartbeatAsync(AdapterBindingHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);

        var now = heartbeat.LastSeenAt.ToString("O");
        var expiresAt = heartbeat.ExpiresAt?.ToString("O");

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gateway_adapter_bindings (
                adapter_kind, adapter_instance_id, agent_identity, user_identity, project_id, role,
                status, capabilities_json, metadata_json, last_seen_at, expires_at, created_at, updated_at
            ) VALUES (
                $adapter_kind, $adapter_instance_id, $agent_identity, $user_identity, $project_id, $role,
                $status, $capabilities_json, $metadata_json, $last_seen_at, $expires_at, $created_at, $updated_at
            )
            ON CONFLICT(
                adapter_kind,
                adapter_instance_id,
                project_id_key,
                agent_identity_key,
                user_identity_key,
                role_key
            ) DO UPDATE SET
                status = excluded.status,
                capabilities_json = excluded.capabilities_json,
                metadata_json = excluded.metadata_json,
                last_seen_at = excluded.last_seen_at,
                expires_at = excluded.expires_at,
                updated_at = excluded.updated_at
            RETURNING id;
            """;

        command.Parameters.AddWithValue("$adapter_kind", heartbeat.AdapterKind);
        command.Parameters.AddWithValue("$adapter_instance_id", heartbeat.AdapterInstanceId);
        command.Parameters.AddWithValue("$agent_identity", DbValue(heartbeat.AgentIdentity));
        command.Parameters.AddWithValue("$user_identity", DbValue(heartbeat.UserIdentity));
        command.Parameters.AddWithValue("$project_id", DbValue(heartbeat.ProjectId));
        command.Parameters.AddWithValue("$role", DbValue(heartbeat.Role));
        command.Parameters.AddWithValue("$status", heartbeat.Status);
        command.Parameters.AddWithValue("$capabilities_json", heartbeat.CapabilitiesJson);
        command.Parameters.AddWithValue("$metadata_json", heartbeat.MetadataJson);
        command.Parameters.AddWithValue("$last_seen_at", now);
        command.Parameters.AddWithValue("$expires_at", DbValue(expiresAt));
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task UpsertBindingSnapshotsAsync(IReadOnlyList<BindingSnapshotWrite> snapshots, DateTimeOffset capturedAt, CancellationToken cancellationToken = default)
    {
        var snapshotId = $"core-{capturedAt:yyyyMMddHHmmss}";
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        foreach (var snapshot in snapshots)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO binding_snapshots (
                    snapshot_id, captured_at, source_den_generation, agent_identity, project_id, role,
                    adapter_kind, adapter_instance_id, transport_endpoint, status, last_seen_at, expires_at, metadata_json
                ) VALUES (
                    $snapshot_id, $captured_at, $source_den_generation, $agent_identity, $project_id, $role,
                    $adapter_kind, $adapter_instance_id, $transport_endpoint, $status, $last_seen_at, $expires_at, $metadata_json
                );
                """;
            command.Parameters.AddWithValue("$snapshot_id", snapshotId);
            command.Parameters.AddWithValue("$captured_at", capturedAt.ToString("O"));
            command.Parameters.AddWithValue("$source_den_generation", DBNull.Value);
            command.Parameters.AddWithValue("$agent_identity", DbValue(snapshot.AgentIdentity));
            command.Parameters.AddWithValue("$project_id", DbValue(snapshot.ProjectId));
            command.Parameters.AddWithValue("$role", DbValue(snapshot.Role));
            command.Parameters.AddWithValue("$adapter_kind", snapshot.AdapterKind);
            command.Parameters.AddWithValue("$adapter_instance_id", snapshot.AdapterInstanceId);
            command.Parameters.AddWithValue("$transport_endpoint", DbValue(snapshot.TransportEndpoint));
            command.Parameters.AddWithValue("$status", snapshot.Status);
            command.Parameters.AddWithValue("$last_seen_at", snapshot.LastSeenAt is null ? DBNull.Value : snapshot.LastSeenAt.Value.ToString("O"));
            command.Parameters.AddWithValue("$expires_at", snapshot.ExpiresAt is null ? DBNull.Value : snapshot.ExpiresAt.Value.ToString("O"));
            command.Parameters.AddWithValue("$metadata_json", snapshot.MetadataJson);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<BindingSnapshotRead>> ListLatestBindingSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at, agent_identity, project_id, role, adapter_kind, adapter_instance_id,
                   transport_endpoint, status, last_seen_at, expires_at, metadata_json
            FROM binding_snapshots
            WHERE snapshot_id = (SELECT snapshot_id FROM binding_snapshots ORDER BY captured_at DESC LIMIT 1)
            ORDER BY adapter_kind, adapter_instance_id;
            """;
        var rows = new List<BindingSnapshotRead>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BindingSnapshotRead(
                CapturedAt: DateTimeOffset.Parse(reader.GetString(0)),
                AgentIdentity: reader.IsDBNull(1) ? null : reader.GetString(1),
                ProjectId: reader.IsDBNull(2) ? null : reader.GetString(2),
                Role: reader.IsDBNull(3) ? null : reader.GetString(3),
                AdapterKind: reader.GetString(4),
                AdapterInstanceId: reader.GetString(5),
                TransportEndpoint: reader.IsDBNull(6) ? null : reader.GetString(6),
                Status: reader.GetString(7),
                LastSeenAt: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                ExpiresAt: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                MetadataJson: reader.GetString(10)));
        }

        return rows;
    }

    public async Task<bool> InsertSentinelEventIfChangedAsync(string eventKind, string? targetIdentity, string payloadJson, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var latest = connection.CreateCommand();
        latest.CommandText = "SELECT event_kind FROM sentinel_events WHERE target_identity IS $target_identity ORDER BY id DESC LIMIT 1;";
        latest.Parameters.AddWithValue("$target_identity", targetIdentity is null ? DBNull.Value : targetIdentity);
        var lastKind = await latest.ExecuteScalarAsync(cancellationToken) as string;
        if (string.Equals(lastKind, eventKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO sentinel_events (event_kind, target_identity, payload_json, created_at)
            VALUES ($event_kind, $target_identity, $payload_json, $created_at);
            """;
        insert.Parameters.AddWithValue("$event_kind", eventKind);
        insert.Parameters.AddWithValue("$target_identity", targetIdentity is null ? DBNull.Value : targetIdentity);
        insert.Parameters.AddWithValue("$payload_json", payloadJson);
        insert.Parameters.AddWithValue("$created_at", createdAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<string?> ReadDeliveryLoopCursorAsync(string source, string? projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cursor_value
            FROM delivery_ingestion_cursors
            WHERE source = $source AND project_id_key = ifnull($project_id, '')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$project_id", DbValue(projectId));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task UpsertDeliveryLoopCursorAsync(string source, string? projectId, string cursorValue, DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_ingestion_cursors (source, project_id, cursor_value, observed_at, updated_at)
            VALUES ($source, $project_id, $cursor_value, $observed_at, $updated_at)
            ON CONFLICT(source, project_id_key) DO UPDATE SET
                cursor_value = excluded.cursor_value,
                observed_at = excluded.observed_at,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$project_id", DbValue(projectId));
        command.Parameters.AddWithValue("$cursor_value", cursorValue);
        command.Parameters.AddWithValue("$observed_at", observedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", observedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeliveryCreateResult> CreateDeliveryRequestAsync(DeliveryCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = request.CreatedAt.ToString("O");
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO delivery_requests (
                source_kind, source_id, source_project_id, target_type, target_identity, project_id, task_id,
                channel_id, delivery_mode, priority, reason, context_summary, context_link, metadata_json,
                status, suppression_reason, dedupe_key, cascade_depth, attempt_count, next_attempt_at,
                expires_at, assignment_id, worker_identity, worker_role, assignment_purpose,
                created_at, updated_at
            ) VALUES (
                $source_kind, $source_id, $source_project_id, $target_type, $target_identity, $project_id, $task_id,
                $channel_id, $delivery_mode, $priority, $reason, $context_summary, $context_link, $metadata_json,
                $status, $suppression_reason, $dedupe_key, $cascade_depth, 0, $next_attempt_at,
                $expires_at, $assignment_id, $worker_identity, $worker_role, $assignment_purpose,
                $created_at, $updated_at
            )
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$source_kind", request.SourceKind);
        command.Parameters.AddWithValue("$source_id", DbValue(request.SourceId));
        command.Parameters.AddWithValue("$source_project_id", DbValue(request.SourceProjectId));
        command.Parameters.AddWithValue("$target_type", request.TargetType);
        command.Parameters.AddWithValue("$target_identity", request.TargetIdentity);
        command.Parameters.AddWithValue("$project_id", DbValue(request.ProjectId));
        command.Parameters.AddWithValue("$task_id", request.TaskId is null ? DBNull.Value : request.TaskId.Value);
        command.Parameters.AddWithValue("$channel_id", DbValue(request.ChannelId));
        command.Parameters.AddWithValue("$delivery_mode", request.DeliveryMode);
        command.Parameters.AddWithValue("$priority", request.Priority);
        command.Parameters.AddWithValue("$reason", DbValue(request.Reason));
        command.Parameters.AddWithValue("$context_summary", DbValue(request.ContextSummary));
        command.Parameters.AddWithValue("$context_link", DbValue(request.ContextLink));
        command.Parameters.AddWithValue("$metadata_json", string.IsNullOrWhiteSpace(request.MetadataJson) ? "{}" : request.MetadataJson);
        command.Parameters.AddWithValue("$status", request.Status);
        command.Parameters.AddWithValue("$suppression_reason", DbValue(request.SuppressionReason));
        command.Parameters.AddWithValue("$dedupe_key", request.DedupeKey);
        command.Parameters.AddWithValue("$cascade_depth", request.CascadeDepth);
        command.Parameters.AddWithValue("$next_attempt_at", request.NextAttemptAt is null ? DBNull.Value : request.NextAttemptAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$expires_at", request.ExpiresAt is null ? DBNull.Value : request.ExpiresAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$assignment_id", DbValue(request.AssignmentId));
        command.Parameters.AddWithValue("$worker_identity", DbValue(request.WorkerIdentity));
        command.Parameters.AddWithValue("$worker_role", DbValue(request.WorkerRole));
        command.Parameters.AddWithValue("$assignment_purpose", DbValue(request.AssignmentPurpose));
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);

        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        if (inserted is not null)
        {
            return new DeliveryCreateResult(Convert.ToInt64(inserted), false);
        }

        await using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT id FROM delivery_requests WHERE dedupe_key = $dedupe_key LIMIT 1;";
        lookup.Parameters.AddWithValue("$dedupe_key", request.DedupeKey);
        var existing = await lookup.ExecuteScalarAsync(cancellationToken);
        return new DeliveryCreateResult(Convert.ToInt64(existing), true);
    }

    public async Task<DeliveryClaimResult> ClaimDeliveriesAsync(DeliveryClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = Math.Clamp(request.Limit, 1, 100);
        var leaseSeconds = Math.Clamp(request.LeaseSeconds, 1, 3600);
        var acceptedModes = request.AcceptedDeliveryModes.Count == 0
            ? ["notify", "wake", "pause", "resume"]
            : request.AcceptedDeliveryModes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var claimedAt = request.ClaimedAt ?? DateTimeOffset.UtcNow;
        var leaseExpiresAt = claimedAt.AddSeconds(leaseSeconds);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);

        try
        {
            var binding = await FindAdapterBindingAsync(connection, request, claimedAt, cancellationToken);
            if (binding is null)
            {
                await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
                return new DeliveryClaimResult([]);
            }

            var candidates = await FindPendingDeliveriesAsync(connection, request, acceptedModes, limit, claimedAt, cancellationToken);
            var claimed = new List<ClaimedDeliveryDto>();
            foreach (var candidate in candidates)
            {
                var attemptNumber = candidate.AttemptCount + 1;
                var updated = await MarkDeliveryClaimedAsync(connection, candidate.Id, attemptNumber, leaseExpiresAt, claimedAt, cancellationToken);
                if (!updated)
                {
                    continue;
                }

                var attemptId = await InsertDeliveryAttemptAsync(
                    connection,
                    candidate.Id,
                    binding.Value.Id,
                    attemptNumber,
                    "delivering",
                    $"{{\"adapter_kind\":\"{EscapeJson(request.AdapterKind)}\",\"adapter_instance_id\":\"{EscapeJson(request.AdapterInstanceId)}\"}}",
                    claimedAt,
                    cancellationToken);

                claimed.Add(new ClaimedDeliveryDto(
                    DeliveryRequestId: candidate.Id,
                    AttemptId: attemptId,
                    AttemptNumber: attemptNumber,
                    AdapterBindingId: binding.Value.Id,
                    TargetType: candidate.TargetType,
                    TargetIdentity: candidate.TargetIdentity,
                    ProjectId: candidate.ProjectId,
                    DeliveryMode: candidate.DeliveryMode,
                    SourceKind: candidate.SourceKind,
                    SourceId: candidate.SourceId,
                    SourceProjectId: candidate.SourceProjectId,
                    ContextSummary: candidate.ContextSummary,
                    ContextLink: candidate.ContextLink,
                    MetadataJson: candidate.MetadataJson,
                    DedupeKey: candidate.DedupeKey,
                    LeaseExpiresAt: leaseExpiresAt,
                    AssignmentId: candidate.AssignmentId,
                    WorkerIdentity: candidate.WorkerIdentity,
                    WorkerRole: candidate.WorkerRole,
                    AssignmentPurpose: candidate.AssignmentPurpose));
            }

            await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
            return new DeliveryClaimResult(claimed);
        }
        catch
        {
            await ExecuteNonQueryAsync(connection, "ROLLBACK;", cancellationToken);
            throw;
        }
    }

    public async Task<DeliveryCallbackResult> ApplyDeliveryCallbackAsync(long deliveryRequestId, string status, DeliveryCallbackRequest callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!AllowedCallbackStatuses.Contains(status))
        {
            throw new ArgumentException($"Unsupported delivery callback status '{status}'.", nameof(status));
        }

        var observedAt = callback.ObservedAt ?? DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);

        try
        {
            var current = await ReadDeliveryStateAsync(connection, deliveryRequestId, cancellationToken);
            if (current is null)
            {
                await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
                return new DeliveryCallbackResult("not_found", false);
            }
            var currentValue = current.Value;

            var requestStatus = status;
            DateTimeOffset? nextAttemptAt = null;
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) && currentValue.AttemptCount < 3)
            {
                requestStatus = "pending";
                var backoffSeconds = Math.Min(300, currentValue.AttemptCount * 60);
                nextAttemptAt = observedAt.AddSeconds(backoffSeconds <= 0 ? 60 : backoffSeconds);
            }

            var attemptStatus = await ReadAttemptStatusAsync(connection, deliveryRequestId, callback.AttemptId, cancellationToken);
            if (string.Equals(currentValue.Status, requestStatus, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attemptStatus, status, StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
                return new DeliveryCallbackResult(requestStatus, false);
            }

            await using (var updateRequest = connection.CreateCommand())
            {
                updateRequest.CommandText = """
                    UPDATE delivery_requests
                    SET status = $status,
                        updated_at = $updated_at,
                        lease_expires_at = NULL,
                        next_attempt_at = $next_attempt_at,
                        completed_at = CASE WHEN $status IN ('completed', 'failed', 'expired') THEN $updated_at ELSE completed_at END
                    WHERE id = $id;
                    """;
                updateRequest.Parameters.AddWithValue("$status", requestStatus);
                updateRequest.Parameters.AddWithValue("$updated_at", observedAt.ToString("O"));
                updateRequest.Parameters.AddWithValue("$next_attempt_at", nextAttemptAt is null ? DBNull.Value : nextAttemptAt.Value.ToString("O"));
                updateRequest.Parameters.AddWithValue("$id", deliveryRequestId);
                await updateRequest.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpdateAttemptFromCallbackAsync(connection, deliveryRequestId, status, callback, observedAt, cancellationToken);
            await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
            return new DeliveryCallbackResult(requestStatus, true);
        }
        catch
        {
            await ExecuteNonQueryAsync(connection, "ROLLBACK;", cancellationToken);
            throw;
        }
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static readonly HashSet<string> AllowedCallbackStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "delivered", "acknowledged", "completed", "failed", "expired"
    };

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await ExecuteNonQueryAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken);
    }

    private static async Task SeedSentinelStateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO sentinel_state (
                id, state, reason, failure_count, success_count, updated_at
            ) VALUES (
                1, 'normal', 'initial', 0, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AdapterBindingRow?> FindAdapterBindingAsync(SqliteConnection connection, DeliveryClaimRequest request, DateTimeOffset claimedAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM gateway_adapter_bindings
            WHERE adapter_kind = $adapter_kind
              AND adapter_instance_id = $adapter_instance_id
              AND status = 'active'
              AND ($project_id IS NULL OR project_id = $project_id OR project_id IS NULL)
              AND ($agent_identity IS NULL OR agent_identity = $agent_identity OR agent_identity IS NULL)
              AND ($role IS NULL OR role = $role OR role IS NULL)
              AND (expires_at IS NULL OR expires_at > $claimed_at)
            ORDER BY id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$adapter_kind", request.AdapterKind);
        command.Parameters.AddWithValue("$adapter_instance_id", request.AdapterInstanceId);
        command.Parameters.AddWithValue("$project_id", DbValue(request.ProjectId));
        command.Parameters.AddWithValue("$agent_identity", DbValue(request.AgentIdentity));
        command.Parameters.AddWithValue("$role", DbValue(request.Role));
        command.Parameters.AddWithValue("$claimed_at", claimedAt.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AdapterBindingRow(reader.GetInt64(0));
    }

    private static async Task<IReadOnlyList<DeliveryRequestRow>> FindPendingDeliveriesAsync(
        SqliteConnection connection,
        DeliveryClaimRequest request,
        IReadOnlyList<string> acceptedModes,
        int limit,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var modeParameters = new List<string>();
        for (var i = 0; i < acceptedModes.Count; i++)
        {
            var name = $"$mode_{i}";
            modeParameters.Add(name);
            command.Parameters.AddWithValue(name, acceptedModes[i]);
        }

        command.CommandText = $"""
            SELECT id, source_kind, source_id, source_project_id, target_type, target_identity, project_id,
                   delivery_mode, context_summary, context_link, metadata_json, dedupe_key, attempt_count,
                   assignment_id, worker_identity, worker_role, assignment_purpose
            FROM delivery_requests
            WHERE status = 'pending'
              AND delivery_mode IN ({string.Join(", ", modeParameters)})
              AND (expires_at IS NULL OR expires_at > $claimed_at)
              AND (next_attempt_at IS NULL OR next_attempt_at <= $claimed_at)
              AND ($project_id IS NULL OR project_id = $project_id OR project_id IS NULL)
              AND (
                    (target_type = 'agent' AND $agent_identity IS NOT NULL AND target_identity = $agent_identity)
                 OR (target_type = 'role' AND $role IS NOT NULL AND target_identity = $role)
                 OR (target_type = 'instance' AND target_identity = $adapter_instance_id)
                 OR (target_type = 'adapter' AND target_identity IN ($adapter_kind, $adapter_instance_id))
              )
            ORDER BY priority ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$claimed_at", claimedAt.ToString("O"));
        command.Parameters.AddWithValue("$project_id", DbValue(request.ProjectId));
        command.Parameters.AddWithValue("$agent_identity", DbValue(request.AgentIdentity));
        command.Parameters.AddWithValue("$role", DbValue(request.Role));
        command.Parameters.AddWithValue("$adapter_instance_id", request.AdapterInstanceId);
        command.Parameters.AddWithValue("$adapter_kind", request.AdapterKind);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<DeliveryRequestRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DeliveryRequestRow(
                Id: reader.GetInt64(0),
                SourceKind: reader.GetString(1),
                SourceId: reader.IsDBNull(2) ? null : reader.GetString(2),
                SourceProjectId: reader.IsDBNull(3) ? null : reader.GetString(3),
                TargetType: reader.GetString(4),
                TargetIdentity: reader.GetString(5),
                ProjectId: reader.IsDBNull(6) ? null : reader.GetString(6),
                DeliveryMode: reader.GetString(7),
                ContextSummary: reader.IsDBNull(8) ? null : reader.GetString(8),
                ContextLink: reader.IsDBNull(9) ? null : reader.GetString(9),
                MetadataJson: reader.IsDBNull(10) ? "{}" : reader.GetString(10),
                DedupeKey: reader.GetString(11),
                AttemptCount: reader.GetInt32(12),
                AssignmentId: reader.IsDBNull(13) ? null : reader.GetString(13),
                WorkerIdentity: reader.IsDBNull(14) ? null : reader.GetString(14),
                WorkerRole: reader.IsDBNull(15) ? null : reader.GetString(15),
                AssignmentPurpose: reader.IsDBNull(16) ? null : reader.GetString(16)));
        }

        return rows;
    }

    private static async Task<bool> MarkDeliveryClaimedAsync(SqliteConnection connection, long deliveryRequestId, int attemptNumber, DateTimeOffset leaseExpiresAt, DateTimeOffset claimedAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE delivery_requests
            SET status = 'delivering',
                attempt_count = $attempt_count,
                lease_expires_at = $lease_expires_at,
                claimed_at = $claimed_at,
                updated_at = $updated_at
            WHERE id = $id AND status = 'pending';
            """;
        command.Parameters.AddWithValue("$attempt_count", attemptNumber);
        command.Parameters.AddWithValue("$lease_expires_at", leaseExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("$claimed_at", claimedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", claimedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", deliveryRequestId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<long> InsertDeliveryAttemptAsync(SqliteConnection connection, long deliveryRequestId, long adapterBindingId, int attemptNumber, string status, string payloadJson, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_attempts (
                delivery_request_id, adapter_binding_id, attempt_number, status, payload_json, created_at
            ) VALUES (
                $delivery_request_id, $adapter_binding_id, $attempt_number, $status, $payload_json, $created_at
            )
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$delivery_request_id", deliveryRequestId);
        command.Parameters.AddWithValue("$adapter_binding_id", adapterBindingId);
        command.Parameters.AddWithValue("$attempt_number", attemptNumber);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$created_at", createdAt.ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private static async Task<DeliveryStateRow?> ReadDeliveryStateAsync(SqliteConnection connection, long deliveryRequestId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, attempt_count FROM delivery_requests WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deliveryRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeliveryStateRow(reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<string?> ReadAttemptStatusAsync(SqliteConnection connection, long deliveryRequestId, long? attemptId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = attemptId is null
            ? "SELECT status FROM delivery_attempts WHERE delivery_request_id = $delivery_request_id ORDER BY attempt_number DESC LIMIT 1;"
            : "SELECT status FROM delivery_attempts WHERE delivery_request_id = $delivery_request_id AND id = $attempt_id LIMIT 1;";
        command.Parameters.AddWithValue("$delivery_request_id", deliveryRequestId);
        if (attemptId is not null)
        {
            command.Parameters.AddWithValue("$attempt_id", attemptId.Value);
        }

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task UpdateAttemptFromCallbackAsync(SqliteConnection connection, long deliveryRequestId, string status, DeliveryCallbackRequest callback, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = callback.AttemptId is null
            ? """
              UPDATE delivery_attempts
              SET status = $status, ack_kind = $ack_kind, external_message_id = $external_message_id,
                  session_id = $session_id, observed_at = $observed_at, error_code = $error_code,
                  error_message = $error_message, payload_json = $payload_json
              WHERE id = (SELECT id FROM delivery_attempts WHERE delivery_request_id = $delivery_request_id ORDER BY attempt_number DESC LIMIT 1);
              """
            : """
              UPDATE delivery_attempts
              SET status = $status, ack_kind = $ack_kind, external_message_id = $external_message_id,
                  session_id = $session_id, observed_at = $observed_at, error_code = $error_code,
                  error_message = $error_message, payload_json = $payload_json
              WHERE delivery_request_id = $delivery_request_id AND id = $attempt_id;
              """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$ack_kind", DbValue(callback.AckKind));
        command.Parameters.AddWithValue("$external_message_id", DbValue(callback.ExternalMessageId));
        command.Parameters.AddWithValue("$session_id", DbValue(callback.SessionId));
        command.Parameters.AddWithValue("$observed_at", observedAt.ToString("O"));
        command.Parameters.AddWithValue("$error_code", DbValue(callback.ErrorCode));
        command.Parameters.AddWithValue("$error_message", DbValue(callback.ErrorMessage));
        command.Parameters.AddWithValue("$payload_json", string.IsNullOrWhiteSpace(callback.MetadataJson) ? "{}" : callback.MetadataJson);
        command.Parameters.AddWithValue("$delivery_request_id", deliveryRequestId);
        if (callback.AttemptId is not null)
        {
            command.Parameters.AddWithValue("$attempt_id", callback.AttemptId.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private readonly record struct AdapterBindingRow(long Id);
    private readonly record struct DeliveryStateRow(string Status, int AttemptCount);

    private sealed record DeliveryRequestRow(
        long Id,
        string SourceKind,
        string? SourceId,
        string? SourceProjectId,
        string TargetType,
        string TargetIdentity,
        string? ProjectId,
        string DeliveryMode,
        string? ContextSummary,
        string? ContextLink,
        string MetadataJson,
        string DedupeKey,
        int AttemptCount,
        string? AssignmentId,
        string? WorkerIdentity,
        string? WorkerRole,
        string? AssignmentPurpose);

    private static readonly string[] SchemaStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS gateway_adapter_bindings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            adapter_kind TEXT NOT NULL,
            adapter_instance_id TEXT NOT NULL,
            agent_identity TEXT NULL,
            user_identity TEXT NULL,
            project_id TEXT NULL,
            role TEXT NULL,
            project_id_key TEXT GENERATED ALWAYS AS (ifnull(project_id, '')) STORED,
            agent_identity_key TEXT GENERATED ALWAYS AS (ifnull(agent_identity, '')) STORED,
            user_identity_key TEXT GENERATED ALWAYS AS (ifnull(user_identity, '')) STORED,
            role_key TEXT GENERATED ALWAYS AS (ifnull(role, '')) STORED,
            status TEXT NOT NULL CHECK (status IN ('active', 'degraded', 'inactive')),
            capabilities_json TEXT NOT NULL DEFAULT '{}',
            metadata_json TEXT NOT NULL DEFAULT '{}',
            last_seen_at TEXT NULL,
            expires_at TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(adapter_kind, adapter_instance_id, project_id_key, agent_identity_key, user_identity_key, role_key)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS delivery_requests (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_kind TEXT NOT NULL,
            source_id TEXT NULL,
            source_project_id TEXT NULL,
            target_type TEXT NOT NULL CHECK (target_type IN ('agent', 'role', 'instance', 'adapter', 'user')),
            target_identity TEXT NOT NULL,
            project_id TEXT NULL,
            task_id INTEGER NULL,
            channel_id TEXT NULL,
            delivery_mode TEXT NOT NULL CHECK (delivery_mode IN ('record_only', 'notify', 'wake', 'pause', 'resume')),
            priority INTEGER NOT NULL DEFAULT 3,
            reason TEXT NULL,
            context_summary TEXT NULL,
            context_link TEXT NULL,
            metadata_json TEXT NOT NULL DEFAULT '{}',
            status TEXT NOT NULL CHECK (status IN ('pending', 'suppressed', 'delivering', 'delivered', 'acknowledged', 'completed', 'failed', 'expired')),
            suppression_reason TEXT NULL,
            dedupe_key TEXT NOT NULL UNIQUE,
            cascade_depth INTEGER NOT NULL DEFAULT 0,
            attempt_count INTEGER NOT NULL DEFAULT 0,
            lease_expires_at TEXT NULL,
            next_attempt_at TEXT NULL,
            expires_at TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS delivery_attempts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            delivery_request_id INTEGER NOT NULL REFERENCES delivery_requests(id) ON DELETE CASCADE,
            adapter_binding_id INTEGER NULL REFERENCES gateway_adapter_bindings(id) ON DELETE SET NULL,
            attempt_number INTEGER NOT NULL,
            status TEXT NOT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            ack_kind TEXT NULL,
            external_message_id TEXT NULL,
            session_id TEXT NULL,
            observed_at TEXT NULL,
            payload_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            UNIQUE(delivery_request_id, attempt_number)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS sentinel_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            state TEXT NOT NULL CHECK (state IN ('normal', 'planned_pause_pending', 'pausing', 'paused_den_maintenance', 'degraded', 'down_detected', 'waiting_for_stable', 'resume_pending', 'normal_after_resume')),
            reason TEXT NULL,
            last_den_health_json TEXT NULL,
            last_den_healthy_at TEXT NULL,
            failure_count INTEGER NOT NULL DEFAULT 0,
            success_count INTEGER NOT NULL DEFAULT 0,
            current_maintenance_id TEXT NULL,
            updated_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS binding_snapshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            source_den_generation TEXT NULL,
            agent_identity TEXT NULL,
            project_id TEXT NULL,
            role TEXT NULL,
            adapter_kind TEXT NOT NULL,
            adapter_instance_id TEXT NOT NULL,
            transport_endpoint TEXT NULL,
            status TEXT NOT NULL,
            last_seen_at TEXT NULL,
            expires_at TEXT NULL,
            metadata_json TEXT NOT NULL DEFAULT '{}'
        );
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_binding_snapshots_target
        ON binding_snapshots(
            snapshot_id,
            adapter_kind,
            adapter_instance_id,
            ifnull(project_id, ''),
            ifnull(agent_identity, ''),
            ifnull(role, '')
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS sentinel_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            event_kind TEXT NOT NULL,
            target_identity TEXT NULL,
            delivery_request_id INTEGER NULL REFERENCES delivery_requests(id) ON DELETE SET NULL,
            payload_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            reconciled_at TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS maintenance_windows (
            maintenance_id TEXT PRIMARY KEY,
            reason TEXT NOT NULL,
            requested_by TEXT NOT NULL,
            not_before TEXT NOT NULL,
            expected_until TEXT NULL,
            state TEXT NOT NULL,
            nonce TEXT NULL,
            auth_metadata_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS delivery_ingestion_cursors (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            project_id TEXT NULL,
            project_id_key TEXT GENERATED ALWAYS AS (ifnull(project_id, '')) STORED,
            cursor_value TEXT NOT NULL,
            observed_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(source, project_id_key)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_delivery_requests_status ON delivery_requests(status);",
        "CREATE INDEX IF NOT EXISTS idx_delivery_requests_target ON delivery_requests(target_type, target_identity);",
        "CREATE INDEX IF NOT EXISTS idx_delivery_requests_project ON delivery_requests(project_id);",
        "CREATE INDEX IF NOT EXISTS idx_gateway_adapter_bindings_status ON gateway_adapter_bindings(status);",
        "CREATE INDEX IF NOT EXISTS idx_sentinel_events_reconciled ON sentinel_events(reconciled_at);",
        """
        CREATE TABLE IF NOT EXISTS discord_notifications (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            dedupe_key TEXT NOT NULL UNIQUE,
            target_agent_identity TEXT NOT NULL,
            body TEXT NOT NULL,
            body_truncated INTEGER NOT NULL DEFAULT 0,
            source_channel_id TEXT NOT NULL,
            source_message_id TEXT NOT NULL,
            source_project_id TEXT NULL,
            requester TEXT NOT NULL,
            urgency TEXT NULL,
            discord_channel_id TEXT NULL,
            discord_thread_id TEXT NULL,
            mention_user_id TEXT NULL,
            wake_by_mention INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'pending',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS discord_notification_attempts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            notification_id INTEGER NOT NULL REFERENCES discord_notifications(id) ON DELETE CASCADE,
            attempt_number INTEGER NOT NULL,
            status TEXT NOT NULL,
            discord_message_id TEXT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            payload_json TEXT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(notification_id, attempt_number)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_discord_notifications_dedupe ON discord_notifications(dedupe_key);",
        "CREATE INDEX IF NOT EXISTS idx_discord_notifications_target ON discord_notifications(target_agent_identity, status);",
        "CREATE INDEX IF NOT EXISTS idx_discord_notification_attempts_notif ON discord_notification_attempts(notification_id);"
    ];
}

public sealed record AdapterBindingHeartbeat(
    string AdapterKind,
    string AdapterInstanceId,
    string? AgentIdentity,
    string? UserIdentity,
    string? ProjectId,
    string? Role,
    string Status,
    string CapabilitiesJson,
    string MetadataJson,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ExpiresAt);

public sealed record DeliveryClaimRequest(
    [property: JsonPropertyName("adapter_kind")] string AdapterKind,
    [property: JsonPropertyName("adapter_instance_id")] string AdapterInstanceId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("agent_identity")] string? AgentIdentity,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("accepted_delivery_modes")] IReadOnlyList<string> AcceptedDeliveryModes,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds,
    [property: JsonPropertyName("claimed_at")] DateTimeOffset? ClaimedAt = null);

public sealed record DeliveryClaimResult([property: JsonPropertyName("deliveries")] IReadOnlyList<ClaimedDeliveryDto> Deliveries);

public sealed record ClaimedDeliveryDto(
    [property: JsonPropertyName("delivery_request_id")] long DeliveryRequestId,
    [property: JsonPropertyName("attempt_id")] long AttemptId,
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("adapter_binding_id")] long AdapterBindingId,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_identity")] string TargetIdentity,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("delivery_mode")] string DeliveryMode,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("source_id")] string? SourceId,
    [property: JsonPropertyName("source_project_id")] string? SourceProjectId,
    [property: JsonPropertyName("context_summary")] string? ContextSummary,
    [property: JsonPropertyName("context_link")] string? ContextLink,
    [property: JsonPropertyName("metadata_json")] string MetadataJson,
    [property: JsonPropertyName("dedupe_key")] string DedupeKey,
    [property: JsonPropertyName("lease_expires_at")] DateTimeOffset LeaseExpiresAt,
    [property: JsonPropertyName("assignment_id")] string? AssignmentId = null,
    [property: JsonPropertyName("worker_identity")] string? WorkerIdentity = null,
    [property: JsonPropertyName("worker_role")] string? WorkerRole = null,
    [property: JsonPropertyName("assignment_purpose")] string? AssignmentPurpose = null);

public sealed record BindingSnapshotWrite(string AdapterKind, string AdapterInstanceId, string? AgentIdentity, string? ProjectId, string? Role, string Status, string? TransportEndpoint, DateTimeOffset? LastSeenAt, DateTimeOffset? ExpiresAt, string MetadataJson);
public sealed record BindingSnapshotRead(DateTimeOffset CapturedAt, string? AgentIdentity, string? ProjectId, string? Role, string AdapterKind, string AdapterInstanceId, string? TransportEndpoint, string Status, DateTimeOffset? LastSeenAt, DateTimeOffset? ExpiresAt, string MetadataJson);

public sealed record DeliveryCreateRequest(
    string SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string TargetType,
    string TargetIdentity,
    string? ProjectId,
    int? TaskId,
    string? ChannelId,
    string DeliveryMode,
    int Priority,
    string? Reason,
    string? ContextSummary,
    string? ContextLink,
    string MetadataJson,
    string Status,
    string? SuppressionReason,
    string DedupeKey,
    int CascadeDepth,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string? AssignmentId = null,
    string? WorkerIdentity = null,
    string? WorkerRole = null,
    string? AssignmentPurpose = null);

public sealed record DeliveryCreateResult(long DeliveryRequestId, bool AlreadyExisted);

public sealed record DeliveryCallbackRequest(
    [property: JsonPropertyName("attempt_id")] long? AttemptId,
    [property: JsonPropertyName("ack_kind")] string? AckKind,
    [property: JsonPropertyName("adapter_kind")] string? AdapterKind,
    [property: JsonPropertyName("adapter_instance_id")] string? AdapterInstanceId,
    [property: JsonPropertyName("external_message_id")] string? ExternalMessageId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt,
    [property: JsonPropertyName("metadata_json")] string? MetadataJson,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("error_message")] string? ErrorMessage);

public sealed record DeliveryCallbackResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("changed")] bool Changed);
