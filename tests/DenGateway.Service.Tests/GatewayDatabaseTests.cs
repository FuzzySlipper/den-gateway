using DenGateway.Service.Persistence;
using Microsoft.Data.Sqlite;

namespace DenGateway.Service.Tests;

public class GatewayDatabaseTests
{
    [Fact]
    public async Task InitializeAsyncCreatesExpectedGatewayTablesAndIsIdempotent()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);

        await database.InitializeAsync();
        await database.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        var tables = await ReadTableNamesAsync(connection);

        Assert.Contains("gateway_adapter_bindings", tables);
        Assert.Contains("delivery_requests", tables);
        Assert.Contains("delivery_attempts", tables);
        Assert.Contains("sentinel_state", tables);
        Assert.Contains("binding_snapshots", tables);
        Assert.Contains("sentinel_events", tables);
        Assert.Contains("maintenance_windows", tables);
    }

    [Fact]
    public async Task DeliveryRequestDedupeKeyIsUnique()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        await InsertDeliveryRequestAsync(connection, "dedupe:1");
        var duplicateError = await Assert.ThrowsAsync<SqliteException>(() => InsertDeliveryRequestAsync(connection, "dedupe:1"));

        Assert.Equal(19, duplicateError.SqliteErrorCode);
    }

    [Fact]
    public async Task UpsertAdapterBindingHeartbeatCreatesAndUpdatesBinding()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();

        var firstSeen = DateTimeOffset.Parse("2026-05-11T12:00:00Z");
        var secondSeen = DateTimeOffset.Parse("2026-05-11T12:05:00Z");

        var firstId = await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            AdapterKind: "test",
            AdapterInstanceId: "adapter-1",
            AgentIdentity: "den-gateway-runner",
            UserIdentity: null,
            ProjectId: "den-gateway",
            Role: "runner",
            Status: "active",
            CapabilitiesJson: "{\"wake\":true}",
            MetadataJson: "{\"transport\":\"test\"}",
            LastSeenAt: firstSeen,
            ExpiresAt: firstSeen.AddHours(1)));

        var secondId = await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            AdapterKind: "test",
            AdapterInstanceId: "adapter-1",
            AgentIdentity: "den-gateway-runner",
            UserIdentity: null,
            ProjectId: "den-gateway",
            Role: "runner",
            Status: "degraded",
            CapabilitiesJson: "{\"wake\":false}",
            MetadataJson: "{\"transport\":\"test\",\"note\":\"updated\"}",
            LastSeenAt: secondSeen,
            ExpiresAt: secondSeen.AddHours(1)));

        Assert.Equal(firstId, secondId);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, capabilities_json, metadata_json, last_seen_at
            FROM gateway_adapter_bindings
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", firstId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("degraded", reader.GetString(0));
        Assert.Equal("{\"wake\":false}", reader.GetString(1));
        Assert.Contains("updated", reader.GetString(2));
        Assert.Equal("2026-05-11T12:05:00.0000000+00:00", reader.GetString(3));
        Assert.False(await reader.ReadAsync());
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "den-gateway-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "den-gateway.db");
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task InsertDeliveryRequestAsync(SqliteConnection connection, string dedupeKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_requests (
                source_kind, source_id, target_type, target_identity, delivery_mode, priority,
                status, dedupe_key, attempt_count, cascade_depth, created_at, updated_at
            ) VALUES (
                'task_message', '123', 'agent', 'den-gateway-runner', 'wake', 3,
                'pending', $dedupe_key, 0, 0, '2026-05-11T12:00:00Z', '2026-05-11T12:00:00Z'
            )
            """;
        command.Parameters.AddWithValue("$dedupe_key", dedupeKey);
        await command.ExecuteNonQueryAsync();
    }
}
