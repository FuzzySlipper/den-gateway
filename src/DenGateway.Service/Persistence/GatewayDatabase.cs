using Microsoft.Data.Sqlite;

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

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            status TEXT NOT NULL CHECK (status IN ('pending', 'suppressed', 'delivering', 'delivered', 'acknowledged', 'completed', 'failed', 'expired')),
            suppression_reason TEXT NULL,
            dedupe_key TEXT NOT NULL UNIQUE,
            cascade_depth INTEGER NOT NULL DEFAULT 0,
            attempt_count INTEGER NOT NULL DEFAULT 0,
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
        "CREATE INDEX IF NOT EXISTS idx_delivery_requests_status ON delivery_requests(status);",
        "CREATE INDEX IF NOT EXISTS idx_delivery_requests_target ON delivery_requests(target_type, target_identity);",
        "CREATE INDEX IF NOT EXISTS idx_delivery_requests_project ON delivery_requests(project_id);",
        "CREATE INDEX IF NOT EXISTS idx_gateway_adapter_bindings_status ON gateway_adapter_bindings(status);",
        "CREATE INDEX IF NOT EXISTS idx_sentinel_events_reconciled ON sentinel_events(reconciled_at);"
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
