using Microsoft.Data.Sqlite;

namespace DenGateway.Service.DiscordBridge;

/// <summary>SQLite repository for Discord notification records and attempts.</summary>
public sealed class DiscordNotificationRepository
{
    private readonly string _databasePath;

    public DiscordNotificationRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>Create the discord_notifications and discord_notification_attempts tables if they don't exist.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
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

            CREATE INDEX IF NOT EXISTS idx_discord_notifications_dedupe
                ON discord_notifications(dedupe_key);

            CREATE INDEX IF NOT EXISTS idx_discord_notifications_target
                ON discord_notifications(target_agent_identity, status);

            CREATE INDEX IF NOT EXISTS idx_discord_notification_attempts_notif
                ON discord_notification_attempts(notification_id);
            """;

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Try to insert a notification record. Returns the new id, or null if a duplicate dedupe_key exists.
    /// </summary>
    public async Task<long?> TryInsertNotificationAsync(
        string dedupeKey,
        string targetAgentIdentity,
        string body,
        bool bodyTruncated,
        string sourceChannelId,
        string sourceMessageId,
        string? sourceProjectId,
        string requester,
        string? urgency,
        string? discordChannelId,
        string? discordThreadId,
        string? mentionUserId,
        bool wakeByMention,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO discord_notifications (
                dedupe_key, target_agent_identity, body, body_truncated,
                source_channel_id, source_message_id, source_project_id,
                requester, urgency,
                discord_channel_id, discord_thread_id,
                mention_user_id, wake_by_mention,
                status, created_at, updated_at
            ) VALUES (
                $dedupe_key, $target_agent_identity, $body, $body_truncated,
                $source_channel_id, $source_message_id, $source_project_id,
                $requester, $urgency,
                $discord_channel_id, $discord_thread_id,
                $mention_user_id, $wake_by_mention,
                'pending', $now, $now
            )
            RETURNING id;
            """;

        cmd.Parameters.AddWithValue("$dedupe_key", dedupeKey);
        cmd.Parameters.AddWithValue("$target_agent_identity", targetAgentIdentity);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$body_truncated", bodyTruncated ? 1 : 0);
        cmd.Parameters.AddWithValue("$source_channel_id", sourceChannelId);
        cmd.Parameters.AddWithValue("$source_message_id", sourceMessageId);
        cmd.Parameters.AddWithValue("$source_project_id", DbValue(sourceProjectId));
        cmd.Parameters.AddWithValue("$requester", requester);
        cmd.Parameters.AddWithValue("$urgency", DbValue(urgency));
        cmd.Parameters.AddWithValue("$discord_channel_id", DbValue(discordChannelId));
        cmd.Parameters.AddWithValue("$discord_thread_id", DbValue(discordThreadId));
        cmd.Parameters.AddWithValue("$mention_user_id", DbValue(mentionUserId));
        cmd.Parameters.AddWithValue("$wake_by_mention", wakeByMention ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(cancellationToken);

        if (result is null)
            return null;

        return Convert.ToInt64(result);
    }

    /// <summary>Look up the existing notification id for a dedupe key (for deduped responses).</summary>
    public async Task<long?> FindNotificationByDedupeKeyAsync(string dedupeKey, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM discord_notifications WHERE dedupe_key = $dedupe_key LIMIT 1;";
        cmd.Parameters.AddWithValue("$dedupe_key", dedupeKey);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null ? Convert.ToInt64(result) : null;
    }

    /// <summary>Check if this target agent has had a successful notification sent within the cooldown window.</summary>
    public async Task<bool> IsInCooldownAsync(string targetAgentIdentity, int cooldownSeconds, DateTimeOffset now, long? excludeNotificationId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        var cutoff = now.AddSeconds(-cooldownSeconds).ToString("O");

        await using var cmd = connection.CreateCommand();

        var whereClause = excludeNotificationId is null
            ? "WHERE target_agent_identity = $target AND status IN ('pending', 'sent') AND created_at > $cutoff"
            : "WHERE target_agent_identity = $target AND status IN ('pending', 'sent') AND created_at > $cutoff AND id != $exclude_id";

        cmd.CommandText = $"""
            SELECT 1 FROM discord_notifications
            {whereClause}
            LIMIT 1;
            """;

        cmd.Parameters.AddWithValue("$target", targetAgentIdentity);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        if (excludeNotificationId is not null)
        {
            cmd.Parameters.AddWithValue("$exclude_id", excludeNotificationId.Value);
        }

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    /// <summary>Update notification status and record an attempt.</summary>
    public async Task<long> RecordAttemptAsync(
        long notificationId,
        int attemptNumber,
        string status,
        string? discordMessageId,
        string? errorCode,
        string? errorMessage,
        string? payloadJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);

        try
        {
            // Update notification status
            await using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.CommandText = """
                    UPDATE discord_notifications
                    SET status = $status, updated_at = $now
                    WHERE id = $id AND status IN ('pending', 'sent');
                    """;
                updateCmd.Parameters.AddWithValue("$status", status);
                updateCmd.Parameters.AddWithValue("$now", now.ToString("O"));
                updateCmd.Parameters.AddWithValue("$id", notificationId);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Insert attempt
            await using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO discord_notification_attempts (
                    notification_id, attempt_number, status,
                    discord_message_id, error_code, error_message,
                    payload_json, created_at
                ) VALUES (
                    $notification_id, $attempt_number, $status,
                    $discord_message_id, $error_code, $error_message,
                    $payload_json, $now
                )
                RETURNING id;
                """;

            insertCmd.Parameters.AddWithValue("$notification_id", notificationId);
            insertCmd.Parameters.AddWithValue("$attempt_number", attemptNumber);
            insertCmd.Parameters.AddWithValue("$status", status);
            insertCmd.Parameters.AddWithValue("$discord_message_id", DbValue(discordMessageId));
            insertCmd.Parameters.AddWithValue("$error_code", DbValue(errorCode));
            insertCmd.Parameters.AddWithValue("$error_message", DbValue(errorMessage));
            insertCmd.Parameters.AddWithValue("$payload_json", DbValue(payloadJson));
            insertCmd.Parameters.AddWithValue("$now", now.ToString("O"));

            var result = await insertCmd.ExecuteScalarAsync(cancellationToken);

            await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken);
            return Convert.ToInt64(result!);
        }
        catch
        {
            await ExecuteNonQueryAsync(connection, "ROLLBACK;", cancellationToken);
            throw;
        }
    }

    /// <summary>Get the count of recent attempts for dedupe-key-based cooldown in the same notification.</summary>
    public async Task<int> GetAttemptCountAsync(long notificationId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM discord_notification_attempts WHERE notification_id = $id;";
        cmd.Parameters.AddWithValue("$id", notificationId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = commandText;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
