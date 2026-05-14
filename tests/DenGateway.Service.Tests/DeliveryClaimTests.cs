using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DenGateway.Service.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenGateway.Service.Tests;

public class DeliveryClaimTests
{
    [Fact]
    public async Task ClaimPendingDeliveriesAtomicallyTransitionsRequestsAndCreatesLeasedAttempts()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var bindingId = await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:den-k8:den-gateway-runner:gateway-main",
            AgentIdentity: "den-gateway-runner",
            UserIdentity: null,
            ProjectId: "den-gateway",
            Role: "runner",
            Status: "active",
            CapabilitiesJson: "{\"delivery_modes\":[\"wake\",\"pause\"]}",
            MetadataJson: "{}",
            LastSeenAt: DateTimeOffset.Parse("2026-05-13T22:30:00Z"),
            ExpiresAt: DateTimeOffset.Parse("2026-05-13T23:30:00Z")));
        var deliveryId = await InsertDeliveryRequestAsync(databasePath, "den-gateway-runner", "wake", "dedupe:claim:1");

        var result = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:den-k8:den-gateway-runner:gateway-main",
            ProjectId: "den-gateway",
            AgentIdentity: "den-gateway-runner",
            Role: "runner",
            AcceptedDeliveryModes: ["wake"],
            Limit: 5,
            LeaseSeconds: 60,
            ClaimedAt: DateTimeOffset.Parse("2026-05-13T22:31:00Z")));

        var claim = Assert.Single(result.Deliveries);
        Assert.Equal(deliveryId, claim.DeliveryRequestId);
        Assert.Equal(bindingId, claim.AdapterBindingId);
        Assert.Equal(1, claim.AttemptNumber);
        Assert.True(claim.AttemptId > 0);
        Assert.Equal("wake", claim.DeliveryMode);
        Assert.Equal("task_message", claim.SourceKind);
        Assert.Equal("123", claim.SourceId);
        Assert.Equal("den://project/den-gateway/task/1391", claim.ContextLink);
        Assert.Equal(DateTimeOffset.Parse("2026-05-13T22:32:00Z"), claim.LeaseExpiresAt);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.status, r.attempt_count, r.lease_expires_at, a.status, a.adapter_binding_id
            FROM delivery_requests r
            JOIN delivery_attempts a ON a.delivery_request_id = r.id
            WHERE r.id = $id
            """;
        command.Parameters.AddWithValue("$id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("delivering", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal("2026-05-13T22:32:00.0000000+00:00", reader.GetString(2));
        Assert.Equal("delivering", reader.GetString(3));
        Assert.Equal(bindingId, reader.GetInt64(4));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task TerminalCallbacksPersistStructuredMetadataAndAreIdempotentForDuplicateRetries()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:den-k8:den-gateway-runner:gateway-main",
            AgentIdentity: "den-gateway-runner",
            UserIdentity: null,
            ProjectId: "den-gateway",
            Role: "runner",
            Status: "active",
            CapabilitiesJson: "{}",
            MetadataJson: "{}",
            LastSeenAt: DateTimeOffset.Parse("2026-05-13T22:30:00Z"),
            ExpiresAt: DateTimeOffset.Parse("2026-05-13T23:30:00Z")));
        var deliveryId = await InsertDeliveryRequestAsync(databasePath, "den-gateway-runner", "wake", "dedupe:callback:1");
        var claimResult = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:den-k8:den-gateway-runner:gateway-main", "den-gateway", "den-gateway-runner", "runner", ["wake"], 1, 60,
            DateTimeOffset.Parse("2026-05-13T22:31:00Z")));
        var attemptId = Assert.Single(claimResult.Deliveries).AttemptId;

        var callback = new DeliveryCallbackRequest(
            AttemptId: attemptId,
            AckKind: "bridge_delivered",
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:den-k8:den-gateway-runner:gateway-main",
            ExternalMessageId: "msg-123",
            SessionId: "session-abc",
            ObservedAt: DateTimeOffset.Parse("2026-05-13T22:31:30Z"),
            MetadataJson: "{\"transport\":\"fake\"}",
            ErrorCode: null,
            ErrorMessage: null);

        var first = await database.ApplyDeliveryCallbackAsync(deliveryId, "completed", callback);
        var second = await database.ApplyDeliveryCallbackAsync(deliveryId, "completed", callback);

        Assert.Equal("completed", first.Status);
        Assert.True(first.Changed);
        Assert.Equal("completed", second.Status);
        Assert.False(second.Changed);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.status, a.status, a.ack_kind, a.external_message_id, a.session_id, a.observed_at, a.payload_json
            FROM delivery_requests r
            JOIN delivery_attempts a ON a.delivery_request_id = r.id
            WHERE r.id = $id
            """;
        command.Parameters.AddWithValue("$id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("completed", reader.GetString(0));
        Assert.Equal("completed", reader.GetString(1));
        Assert.Equal("bridge_delivered", reader.GetString(2));
        Assert.Equal("msg-123", reader.GetString(3));
        Assert.Equal("session-abc", reader.GetString(4));
        Assert.Equal("2026-05-13T22:31:30.0000000+00:00", reader.GetString(5));
        Assert.Contains("fake", reader.GetString(6));
    }

    [Fact]
    public async Task FailCallbackSchedulesRetryUntilMaxAttemptsThenLeavesRequestFailed()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:den-k8:den-gateway-runner:gateway-main", "den-gateway-runner", null, "den-gateway", "runner", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-13T22:30:00Z"), DateTimeOffset.Parse("2026-05-13T23:30:00Z")));
        var deliveryId = await InsertDeliveryRequestAsync(databasePath, "den-gateway-runner", "wake", "dedupe:retry:1");
        var claimResult = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:den-k8:den-gateway-runner:gateway-main", "den-gateway", "den-gateway-runner", "runner", ["wake"], 1, 60,
            DateTimeOffset.Parse("2026-05-13T22:31:00Z")));
        var attemptId = Assert.Single(claimResult.Deliveries).AttemptId;

        var result = await database.ApplyDeliveryCallbackAsync(deliveryId, "failed", new DeliveryCallbackRequest(
            AttemptId: attemptId,
            AckKind: "bridge_failed",
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:den-k8:den-gateway-runner:gateway-main",
            ExternalMessageId: null,
            SessionId: "session-retry",
            ObservedAt: DateTimeOffset.Parse("2026-05-13T22:32:00Z"),
            MetadataJson: "{}",
            ErrorCode: "transport_unavailable",
            ErrorMessage: "adapter offline"));

        Assert.Equal("pending", result.Status);
        Assert.True(result.Changed);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.status, r.next_attempt_at, r.lease_expires_at, a.status, a.error_code
            FROM delivery_requests r
            JOIN delivery_attempts a ON a.delivery_request_id = r.id
            WHERE r.id = $id
            """;
        command.Parameters.AddWithValue("$id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("pending", reader.GetString(0));
        Assert.Equal("2026-05-13T22:33:00.0000000+00:00", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal("failed", reader.GetString(3));
        Assert.Equal("transport_unavailable", reader.GetString(4));
    }

    [Fact]
    public async Task AdapterBindingHeartbeatEndpointUpsertsBindingForSmokeClaims()
    {
        var databasePath = CreateTempDatabasePath();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenGateway:Database:Path"] = databasePath,
                    ["DenGateway:DenCore:UseStub"] = "true",
                    ["DenGateway:DenChannels:UseStub"] = "true"
                });
            }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/adapter-bindings/heartbeat", new
        {
            adapter_kind = "hermes_profile",
            adapter_instance_id = "den-gateway-smoke-local",
            agent_identity = "den-gateway-runner",
            project_id = "den-gateway",
            role = "runner",
            status = "active",
            capabilities_json = "{\"delivery_modes\":[\"wake\"]}",
            metadata_json = "{\"synthetic\":true}",
            last_seen_at = "2026-05-14T06:05:00Z",
            expires_at = "2026-05-14T06:15:00Z"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>();
        Assert.NotNull(body);
        Assert.True(body.BindingId > 0);

        var database = new GatewayDatabase(databasePath);
        var claim = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "den-gateway-smoke-local", "den-gateway", "den-gateway-runner", "runner", ["wake"], 1, 60,
            DateTimeOffset.Parse("2026-05-14T06:06:00Z")));
        Assert.Empty(claim.Deliveries);
    }

    [Fact]
    public async Task ClaimEndpointReturnsClaimedDeliveryDtoWithAttemptIdAndSourcePointers()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:den-k8:den-gateway-runner:gateway-main", "den-gateway-runner", null, "den-gateway", "runner", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-13T22:30:00Z"), DateTimeOffset.Parse("2026-05-13T23:30:00Z")));
        await InsertDeliveryRequestAsync(databasePath, "den-gateway-runner", "wake", "dedupe:endpoint:1");

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenGateway:Database:Path"] = databasePath,
                    ["DenGateway:DenChannels:UseStub"] = "true"
                });
            }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/deliveries/claim", new
        {
            adapter_kind = "hermes_profile",
            adapter_instance_id = "hermes:den-k8:den-gateway-runner:gateway-main",
            project_id = "den-gateway",
            agent_identity = "den-gateway-runner",
            role = "runner",
            accepted_delivery_modes = new[] { "wake" },
            limit = 1,
            lease_seconds = 60,
            claimed_at = "2026-05-13T22:31:00Z"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ClaimEndpointResponse>();
        Assert.NotNull(body);
        var delivery = Assert.Single(body.Deliveries);
        Assert.True(delivery.AttemptId > 0);
        Assert.Equal("wake", delivery.DeliveryMode);
        Assert.Equal("task_message", delivery.SourceKind);
        Assert.Equal("123", delivery.SourceId);
        Assert.Equal("den://project/den-gateway/task/1391", delivery.ContextLink);
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "den-gateway-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "den-gateway.db");
    }

    private static async Task<long> InsertDeliveryRequestAsync(string databasePath, string targetIdentity, string mode, string dedupeKey)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_requests (
                source_kind, source_id, source_project_id, target_type, target_identity, project_id, task_id,
                delivery_mode, priority, reason, context_summary, context_link, metadata_json,
                status, dedupe_key, attempt_count, cascade_depth, created_at, updated_at
            ) VALUES (
                'task_message', '123', 'den-gateway', 'agent', $target_identity, 'den-gateway', 1391,
                $mode, 2, 'explicit_mention', 'wake summary', 'den://project/den-gateway/task/1391', '{"source_pointer":"task/1391"}',
                'pending', $dedupe_key, 0, 0, '2026-05-13T22:30:00Z', '2026-05-13T22:30:00Z'
            )
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$target_identity", targetIdentity);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$dedupe_key", dedupeKey);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private sealed record HeartbeatResponse([property: JsonPropertyName("binding_id")] long BindingId);

    private sealed record ClaimEndpointResponse([property: JsonPropertyName("deliveries")] IReadOnlyList<ClaimedDeliveryDto> Deliveries);
    private sealed record ClaimedDeliveryDto(
        [property: JsonPropertyName("delivery_request_id")] long DeliveryRequestId,
        [property: JsonPropertyName("attempt_id")] long AttemptId,
        [property: JsonPropertyName("delivery_mode")] string DeliveryMode,
        [property: JsonPropertyName("source_kind")] string SourceKind,
        [property: JsonPropertyName("source_id")] string? SourceId,
        [property: JsonPropertyName("context_link")] string? ContextLink);
}
