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

    [Fact]
    public async Task AssignmentMetadataPropagatesFromDeliveryRequestToClaimedDeliveryDto()
    {
        // Verify that assignment_id, worker_identity, worker_role, and
        // assignment_purpose survive from delivery request creation through
        // claim into the ClaimedDeliveryDto.
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

        // Create a delivery request WITH assignment fields
        var createResult = await database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
            SourceKind: "task_message",
            SourceId: "456",
            SourceProjectId: "den-gateway",
            TargetType: "agent",
            TargetIdentity: "den-gateway-runner",
            ProjectId: "den-gateway",
            TaskId: 1723,
            ChannelId: null,
            DeliveryMode: "wake",
            Priority: 2,
            Reason: "worker_assignment",
            ContextSummary: "Assignment wake for task 1723",
            ContextLink: "den://project/den-gateway/task/1723",
            MetadataJson: "{\"source\":\"core\",\"assignment_test\":true}",
            Status: "pending",
            SuppressionReason: null,
            DedupeKey: "dedupe:assignment:1",
            CascadeDepth: 0,
            NextAttemptAt: null,
            ExpiresAt: null,
            CreatedAt: DateTimeOffset.Parse("2026-05-13T22:30:00Z"),
            AssignmentId: "asn-42",
            WorkerIdentity: "spawned-coder",
            WorkerRole: "coder",
            AssignmentPurpose: "implement_task_1723"));

        Assert.False(createResult.AlreadyExisted);

        var claimResult = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:den-k8:den-gateway-runner:gateway-main",
            ProjectId: "den-gateway",
            AgentIdentity: "den-gateway-runner",
            Role: "runner",
            AcceptedDeliveryModes: ["wake"],
            Limit: 5,
            LeaseSeconds: 60,
            ClaimedAt: DateTimeOffset.Parse("2026-05-13T22:31:00Z")));

        var claim = Assert.Single(claimResult.Deliveries);
        Assert.Equal(createResult.DeliveryRequestId, claim.DeliveryRequestId);
        Assert.Equal("asn-42", claim.AssignmentId);
        Assert.Equal("spawned-coder", claim.WorkerIdentity);
        Assert.Equal("coder", claim.WorkerRole);
        Assert.Equal("implement_task_1723", claim.AssignmentPurpose);
    }

    [Fact]
    public async Task ClaimedDeliveryAssignmentFieldsAreOptionalAndNullByDefault()
    {
        // Verify that a delivery request without assignment fields still
        // claims successfully and the DTO omits them.
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

        // No assignment fields in this request
        var deliveryId = await InsertDeliveryRequestAsync(databasePath, "den-gateway-runner", "wake", "dedupe:no-assignment:1");

        var claimResult = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:den-k8:den-gateway-runner:gateway-main",
            "den-gateway", "den-gateway-runner", "runner", ["wake"], 1, 60,
            DateTimeOffset.Parse("2026-05-13T22:31:00Z")));

        var claim = Assert.Single(claimResult.Deliveries);
        Assert.Equal(deliveryId, claim.DeliveryRequestId);
        Assert.Null(claim.AssignmentId);
        Assert.Null(claim.WorkerIdentity);
        Assert.Null(claim.WorkerRole);
        Assert.Null(claim.AssignmentPurpose);
    }

    [Fact]
    public async Task TerminalCallbackPreservesAssignmentMetadataAcrossAttempts()
    {
        // Verify that assignment fields remain accessible via the delivery
        // request after a claim-callback-completed lifecycle.
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

        var createResult = await database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
            SourceKind: "task_message",
            SourceId: "789",
            SourceProjectId: "den-gateway",
            TargetType: "agent",
            TargetIdentity: "den-gateway-runner",
            ProjectId: "den-gateway",
            TaskId: 1723,
            ChannelId: null,
            DeliveryMode: "wake",
            Priority: 2,
            Reason: "worker_assignment",
            ContextSummary: "Assignment callback test",
            ContextLink: "den://project/den-gateway/task/1723",
            MetadataJson: "{}",
            Status: "pending",
            SuppressionReason: null,
            DedupeKey: "dedupe:callback-assignment:1",
            CascadeDepth: 0,
            NextAttemptAt: null,
            ExpiresAt: null,
            CreatedAt: DateTimeOffset.Parse("2026-05-13T22:30:00Z"),
            AssignmentId: "asn-99",
            WorkerIdentity: "test-worker",
            WorkerRole: "reviewer",
            AssignmentPurpose: "verify_delivery"));

        var claimResult = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:den-k8:den-gateway-runner:gateway-main",
            "den-gateway", "den-gateway-runner", "runner", ["wake"], 1, 60,
            DateTimeOffset.Parse("2026-05-13T22:31:00Z")));

        var attemptId = Assert.Single(claimResult.Deliveries).AttemptId;

        var callback = new DeliveryCallbackRequest(
            AttemptId: attemptId,
            AckKind: "completed",
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:den-k8:den-gateway-runner:gateway-main",
            ExternalMessageId: "msg-cb-1",
            SessionId: "session-cb-1",
            ObservedAt: DateTimeOffset.Parse("2026-05-13T22:31:30Z"),
            MetadataJson: "{}",
            ErrorCode: null,
            ErrorMessage: null);

        var cbResult = await database.ApplyDeliveryCallbackAsync(createResult.DeliveryRequestId, "completed", callback);
        Assert.Equal("completed", cbResult.Status);

        // Verify assignment fields are preserved in the delivery_request row
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT assignment_id, worker_identity, worker_role, assignment_purpose, status FROM delivery_requests WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", createResult.DeliveryRequestId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("asn-99", reader.GetString(0));
        Assert.Equal("test-worker", reader.GetString(1));
        Assert.Equal("reviewer", reader.GetString(2));
        Assert.Equal("verify_delivery", reader.GetString(3));
        Assert.Equal("completed", reader.GetString(4));
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

    [Fact]
    public async Task TwoSameProfileBindingsSelectedInstanceClaimsAssignment()
    {
        // Create two bindings sharing the same agent_identity (shared profile)
        // but with distinct adapter_instance_ids (different worker-pool members).
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();

        var bindingA = await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:pool:worker-instance-a",
            AgentIdentity: "shared-worker-pool",
            UserIdentity: null,
            ProjectId: "den-gateway",
            Role: "coder",
            Status: "active",
            CapabilitiesJson: "{\"delivery_modes\":[\"wake\"]}",
            MetadataJson: "{\"agent_instance_id\":\"worker-instance-a\"}",
            LastSeenAt: DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            ExpiresAt: DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        var bindingB = await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:pool:worker-instance-b",
            AgentIdentity: "shared-worker-pool",
            UserIdentity: null,
            ProjectId: "den-gateway",
            Role: "coder",
            Status: "active",
            CapabilitiesJson: "{\"delivery_modes\":[\"wake\"]}",
            MetadataJson: "{\"agent_instance_id\":\"worker-instance-b\"}",
            LastSeenAt: DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            ExpiresAt: DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        Assert.NotEqual(bindingA, bindingB);

        // Create a delivery request with agent_instance_id = "worker-instance-a"
        // (concrete routing to instance A within the shared profile pool).
        var createResult = await database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
            SourceKind: "task_message",
            SourceId: "delivery-123",
            SourceProjectId: "den-gateway",
            TargetType: "agent",
            TargetIdentity: "shared-worker-pool",
            ProjectId: "den-gateway",
            TaskId: 1770,
            ChannelId: null,
            DeliveryMode: "wake",
            Priority: 1,
            Reason: "worker_assignment",
            ContextSummary: "Concrete instance routing test",
            ContextLink: "den://project/den-gateway/task/1770",
            MetadataJson: "{\"test\":\"concrete-instance-routing\"}",
            Status: "pending",
            SuppressionReason: null,
            DedupeKey: "dedupe:concrete-instance-a:1",
            CascadeDepth: 0,
            NextAttemptAt: null,
            ExpiresAt: null,
            CreatedAt: DateTimeOffset.Parse("2026-05-30T00:01:00Z"),
            AssignmentId: "asn-1770",
            WorkerIdentity: "spawned-coder",
            WorkerRole: "coder",
            AssignmentPurpose: "implement_concrete_routing",
            AgentInstanceId: "worker-instance-a",
            PoolMemberId: null));

        Assert.False(createResult.AlreadyExisted);

        // Claim from instance A — should match because agent_instance_id matches
        var claimA = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:pool:worker-instance-a",
            ProjectId: "den-gateway",
            AgentIdentity: "shared-worker-pool",
            Role: "coder",
            AcceptedDeliveryModes: ["wake"],
            Limit: 5,
            LeaseSeconds: 60,
            ClaimedAt: DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            AgentInstanceId: "worker-instance-a"));

        var deliveryA = Assert.Single(claimA.Deliveries);
        Assert.Equal(createResult.DeliveryRequestId, deliveryA.DeliveryRequestId);
        Assert.Equal(bindingA, deliveryA.AdapterBindingId);
        Assert.Equal("worker-instance-a", deliveryA.AgentInstanceId);
        Assert.Null(deliveryA.PoolMemberId);

        // Claim from instance B — should NOT match because the delivery targets instance A
        var claimB = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:pool:worker-instance-b",
            ProjectId: "den-gateway",
            AgentIdentity: "shared-worker-pool",
            Role: "coder",
            AcceptedDeliveryModes: ["wake"],
            Limit: 5,
            LeaseSeconds: 60,
            ClaimedAt: DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            AgentInstanceId: "worker-instance-b"));

        Assert.Empty(claimB.Deliveries);
    }

    [Fact]
    public async Task WrongInstanceCannotClaimDeliveryWithAgentInstanceId()
    {
        // Verify that a claim with a different agent_instance_id than what
        // the delivery request specifies is correctly rejected.
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();

        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:target-instance", "shared-worker-pool", null,
            "den-gateway", "coder", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:wrong-instance", "shared-worker-pool", null,
            "den-gateway", "coder", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        // Delivery targets "target-instance" via agent_instance_id
        var createResult = await database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
            SourceKind: "task_message", SourceId: "wrong-instance-1", SourceProjectId: "den-gateway",
            TargetType: "agent", TargetIdentity: "shared-worker-pool", ProjectId: "den-gateway",
            TaskId: 1770, ChannelId: null, DeliveryMode: "wake", Priority: 1,
            Reason: "test", ContextSummary: "Wrong instance test",
            ContextLink: "den://project/den-gateway/task/1770",
            MetadataJson: "{}", Status: "pending", SuppressionReason: null,
            DedupeKey: "dedupe:wrong-instance:1", CascadeDepth: 0,
            NextAttemptAt: null, ExpiresAt: null,
            CreatedAt: DateTimeOffset.Parse("2026-05-30T00:01:00Z"),
            AgentInstanceId: "target-instance"));

        // Claim from "wrong-instance" should NOT match
        var wrongClaim = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:pool:wrong-instance", "den-gateway", "shared-worker-pool",
            "coder", ["wake"], 5, 60,
            DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            AgentInstanceId: "wrong-instance"));

        Assert.Empty(wrongClaim.Deliveries);

        // Claim from "target-instance" should match
        var correctClaim = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:pool:target-instance", "den-gateway", "shared-worker-pool",
            "coder", ["wake"], 5, 60,
            DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            AgentInstanceId: "target-instance"));

        var delivery = Assert.Single(correctClaim.Deliveries);
        Assert.Equal(createResult.DeliveryRequestId, delivery.DeliveryRequestId);
        Assert.Equal("target-instance", delivery.AgentInstanceId);
    }

    [Fact]
    public async Task PoolMemberIdRoutingRoutesDeliveryToCorrectInstance()
    {
        // Verify pool_member_id routing similar to agent_instance_id.
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();

        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:p1", "pool-profile", null,
            "den-gateway", "worker", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:p2", "pool-profile", null,
            "den-gateway", "worker", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        var createResult = await database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
            SourceKind: "task_message", SourceId: "pool-routing-1", SourceProjectId: "den-gateway",
            TargetType: "agent", TargetIdentity: "pool-profile", ProjectId: "den-gateway",
            TaskId: 1770, ChannelId: null, DeliveryMode: "wake", Priority: 1,
            Reason: "pool_test", ContextSummary: "Pool member routing test",
            ContextLink: "den://project/den-gateway/task/1770",
            MetadataJson: "{}", Status: "pending", SuppressionReason: null,
            DedupeKey: "dedupe:pool-member:1", CascadeDepth: 0,
            NextAttemptAt: null, ExpiresAt: null,
            CreatedAt: DateTimeOffset.Parse("2026-05-30T00:01:00Z"),
            PoolMemberId: "pm-1"));

        // Claim with matching pool_member_id should succeed
        var claimPm1 = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:pool:p1", "den-gateway", "pool-profile",
            "worker", ["wake"], 5, 60,
            DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            PoolMemberId: "pm-1"));

        var delivery = Assert.Single(claimPm1.Deliveries);
        Assert.Equal(createResult.DeliveryRequestId, delivery.DeliveryRequestId);
        Assert.Equal("pm-1", delivery.PoolMemberId);

        // Claim with non-matching pool_member_id should not match
        var claimPm2 = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:pool:p2", "den-gateway", "pool-profile",
            "worker", ["wake"], 5, 60,
            DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            PoolMemberId: "pm-2"));

        Assert.Empty(claimPm2.Deliveries);
    }

    [Fact]
    public async Task GenericProfileDeliveryClaimableByAnyInstanceWithClaimedInstanceEvidence()
    {
        // A delivery request without agent_instance_id targeting a shared profile
        // should be claimable by any live binding for that profile, and the
        // ClaimedDeliveryDto should carry the claiming instance's evidence.
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();

        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:instance-1", "shared-profile", null,
            "den-gateway", "coder", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:instance-2", "shared-profile", null,
            "den-gateway", "coder", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        // No agent_instance_id — generic profile-targeted delivery
        var createResult = await database.CreateDeliveryRequestAsync(new DeliveryCreateRequest(
            SourceKind: "task_message", SourceId: "generic-1", SourceProjectId: "den-gateway",
            TargetType: "agent", TargetIdentity: "shared-profile", ProjectId: "den-gateway",
            TaskId: 1770, ChannelId: null, DeliveryMode: "wake", Priority: 1,
            Reason: "generic_test", ContextSummary: "Generic profile delivery",
            ContextLink: "den://project/den-gateway/task/1770",
            MetadataJson: "{}", Status: "pending", SuppressionReason: null,
            DedupeKey: "dedupe:generic-profile:1", CascadeDepth: 0,
            NextAttemptAt: null, ExpiresAt: null,
            CreatedAt: DateTimeOffset.Parse("2026-05-30T00:01:00Z")));

        // Claim from instance-1 should pick up the generic profile delivery
        var claim1 = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:pool:instance-1", "den-gateway", "shared-profile",
            "coder", ["wake"], 5, 60,
            DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            AgentInstanceId: "instance-1"));

        var delivery1 = Assert.Single(claim1.Deliveries);
        Assert.Equal(createResult.DeliveryRequestId, delivery1.DeliveryRequestId);
        // Generic delivery has no agent_instance_id set — output should be null
        Assert.Null(delivery1.AgentInstanceId);
        Assert.Null(delivery1.PoolMemberId);
        Assert.Equal("shared-profile", delivery1.TargetIdentity);
    }

    [Fact]
    public async Task DeliveryCallbackRejectsWrongInstanceMismatch()
    {
        // Verify that a callback with a mismatched adapter_instance_id is rejected.
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();

        // Create two bindings, one for each instance
        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:claiming-instance", "shared-worker", null,
            "den-gateway", "coder", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        await database.UpsertAdapterBindingHeartbeatAsync(new AdapterBindingHeartbeat(
            "hermes_profile", "hermes:pool:other-instance", "shared-worker", null,
            "den-gateway", "coder", "active", "{}", "{}",
            DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-30T01:00:00Z")));

        // Create delivery and claim from claiming-instance
        var deliveryId = await InsertDeliveryRequestAsync(databasePath, "shared-worker", "wake", "dedupe:callback-mismatch:1");
        var claimResult = await database.ClaimDeliveriesAsync(new DeliveryClaimRequest(
            "hermes_profile", "hermes:pool:claiming-instance", "den-gateway", "shared-worker",
            "coder", ["wake"], 1, 60,
            DateTimeOffset.Parse("2026-05-30T00:01:00Z")));

        var attemptId = Assert.Single(claimResult.Deliveries).AttemptId;

        // Attempt callback from other-instance — should be rejected as instance_mismatch
        var wrongCallback = new DeliveryCallbackRequest(
            AttemptId: attemptId,
            AckKind: "bridge_delivered",
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:pool:other-instance",
            ExternalMessageId: "msg-wrong",
            SessionId: "session-wrong",
            ObservedAt: DateTimeOffset.Parse("2026-05-30T00:02:00Z"),
            MetadataJson: "{}",
            ErrorCode: null,
            ErrorMessage: null);

        var wrongResult = await database.ApplyDeliveryCallbackAsync(deliveryId, "completed", wrongCallback);
        Assert.Equal("instance_mismatch", wrongResult.Status);
        Assert.False(wrongResult.Changed);

        // Callback from claiming-instance should succeed
        var correctCallback = new DeliveryCallbackRequest(
            AttemptId: attemptId,
            AckKind: "bridge_delivered",
            AdapterKind: "hermes_profile",
            AdapterInstanceId: "hermes:pool:claiming-instance",
            ExternalMessageId: "msg-correct",
            SessionId: "session-correct",
            ObservedAt: DateTimeOffset.Parse("2026-05-30T00:02:30Z"),
            MetadataJson: "{}",
            ErrorCode: null,
            ErrorMessage: null);

        var correctResult = await database.ApplyDeliveryCallbackAsync(deliveryId, "completed", correctCallback);
        Assert.Equal("completed", correctResult.Status);
        Assert.True(correctResult.Changed);
    }
}
