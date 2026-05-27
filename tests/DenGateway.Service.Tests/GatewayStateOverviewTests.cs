using System.Net.Http.Json;
using DenGateway.Service.AgentOverview;
using DenGateway.Service.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenGateway.Service.Tests;

public class GatewayStateOverviewTests
{
    private static readonly DateTimeOffset TestNow = DateTimeOffset.Parse("2026-05-27T12:00:00Z");

    [Fact]
    public async Task FreshBindingIdle_NoDeliveries_ReturnsIdleClassification()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "my-agent", null, "den-proj", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(ProjectId: "den-proj", AgentIdentity: "my-agent", Role: "runner");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Single(result.Groups);
            var group = result.Groups[0];
            Assert.Equal("idle", group.Classification);
            Assert.NotNull(group.Binding);
            Assert.True(group.Binding.IsFresh);
            Assert.Equal(0, group.DeliveryCounts.Total);
            Assert.Null(group.Warnings);
            Assert.Equal(1, result.Metadata.TotalGroups);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task MissingBindingWithPendingDelivery_AddsWarningAndClassifiesAsQueued()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "pending",
                dedupeKey: "dedupe:1", createdAt: TestNow.AddMinutes(-10));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "my-agent");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Single(result.Groups);
            var group = result.Groups[0];
            Assert.Equal("queued", group.Classification);
            Assert.Null(group.Binding);
            Assert.NotNull(group.Warnings);
            Assert.Contains("missing_binding", group.Warnings);
            Assert.Equal(1, group.DeliveryCounts.Pending);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeliveringWithValidLease_ClassifiesAsWorking()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "my-agent", null, "den-proj", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "delivering",
                dedupeKey: "dedupe:2", createdAt: TestNow.AddMinutes(-10),
                leaseExpiresAt: TestNow.AddMinutes(30));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "my-agent");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Single(result.Groups);
            var group = result.Groups[0];
            Assert.Equal("working", group.Classification);
            Assert.NotNull(group.Binding);
            Assert.Equal(1, group.DeliveryCounts.Delivering);
            Assert.Equal(0, group.DeliveryCounts.Stuck);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeliveredNotCompleted_ClassifiesAsDeliveredWaitingCompletion()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "my-agent", null, "den-proj", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "delivered",
                dedupeKey: "dedupe:3", createdAt: TestNow.AddMinutes(-5), updatedAt: TestNow.AddMinutes(-5));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "my-agent");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            var group = result.Groups[0];
            Assert.Equal("delivered_waiting_completion", group.Classification);
            Assert.Equal(1, group.DeliveryCounts.DeliveredNotCompleted);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CompletedAndFailedAndSuppressedTerminalDeliveries_WithinCutoff_Counted()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "my-agent", null, "den-proj", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "completed",
                dedupeKey: "dedupe:c1", createdAt: TestNow.AddMinutes(-30), updatedAt: TestNow.AddMinutes(-25));
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "failed",
                dedupeKey: "dedupe:f1", createdAt: TestNow.AddMinutes(-40), updatedAt: TestNow.AddMinutes(-35));
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "suppressed",
                dedupeKey: "dedupe:s1", createdAt: TestNow.AddMinutes(-50), updatedAt: TestNow.AddMinutes(-48));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "my-agent");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            var group = result.Groups[0];
            Assert.Equal(1, group.DeliveryCounts.CompletedRecent);
            Assert.Equal(1, group.DeliveryCounts.FailedRecent);
            Assert.Equal(1, group.DeliveryCounts.SuppressedRecent);
            Assert.Equal(3, group.DeliveryCounts.Total);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task TerminalDeliveriesOlderThanCutoff_Excluded()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "my-agent", null, "den-proj", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            // Completed delivery older than 120 minute cutoff
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "completed",
                dedupeKey: "dedupe:c_old", createdAt: TestNow.AddHours(-3), updatedAt: TestNow.AddHours(-3).AddMinutes(5));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "my-agent", IncludeTerminalMinutes: 120);
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            var group = result.Groups[0];
            Assert.Equal(0, group.DeliveryCounts.CompletedRecent);
            Assert.Equal(0, group.DeliveryCounts.Total);
            Assert.Equal("idle", group.Classification);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeliveringWithExpiredLease_ClassifiesAsStuck()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "my-agent", null, "den-proj", "runner",
                "active", TestNow.AddMinutes(-30), TestNow.AddMinutes(-15)); // binding is stale/expired
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "delivering",
                dedupeKey: "dedupe:stuck1", createdAt: TestNow.AddMinutes(-30),
                leaseExpiresAt: TestNow.AddMinutes(-10)); // lease expired 10 min ago

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "my-agent");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            var group = result.Groups[0];
            Assert.Equal("stuck", group.Classification);
            Assert.Equal(1, group.DeliveryCounts.Stuck);
            Assert.NotNull(group.Binding);
            Assert.False(group.Binding.IsFresh);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeliveredOlderThanTerminalThreshold_NonTerminalStillVisibleAsDeliveredNotCompleted()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "my-agent", null, "den-proj", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedDeliveryAsync(databasePath, "my-agent", "den-proj", "delivered",
                dedupeKey: "dedupe:d_old", createdAt: TestNow.AddHours(-3));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "my-agent", IncludeTerminalMinutes: 120);
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Single(result.Groups);
            var group = result.Groups[0];
            Assert.Equal(1, group.DeliveryCounts.DeliveredNotCompleted);
            Assert.Equal(1, group.DeliveryCounts.Stuck);
            Assert.Equal("stuck", group.Classification);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LimitParameterCapsResults()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            for (var i = 1; i <= 3; i++)
            {
                await SeedBindingAsync(databasePath, "hermes_profile", $"runner-{i}", $"agent-{i}", null, "den-proj", "runner",
                    "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            }

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(ProjectId: "den-proj", Limit: 2);
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Equal(2, result.Groups.Count);
            Assert.Equal(2, result.Metadata.Limit);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task BindsWithoutDeliveriesAndDeliveriesWithoutBindings_MergeIntoOneGroup()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "agent-1", null, "proj-a", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedDeliveryAsync(databasePath, "agent-2", "proj-a", "pending",
                dedupeKey: "dedupe:agent2", createdAt: TestNow.AddMinutes(-10));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(ProjectId: "proj-a");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Equal(2, result.Groups.Count);

            var group1 = result.Groups.First(g => g.AgentIdentity == "agent-1");
            Assert.NotNull(group1.Binding);
            Assert.Equal("idle", group1.Classification);

            var group2 = result.Groups.First(g => g.AgentIdentity == "agent-2");
            Assert.Null(group2.Binding);
            Assert.Equal("queued", group2.Classification);
            Assert.Contains("missing_binding", group2.Warnings!);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task FiltersByProjectId()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "r1", "agent-a", null, "proj-one", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedBindingAsync(databasePath, "hermes_profile", "r2", "agent-b", null, "proj-two", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(ProjectId: "proj-one");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Single(result.Groups);
            Assert.Equal("proj-one", result.Groups[0].ProjectId);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task FiltersByAgentIdentity()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "r1", "agent-a", null, "proj-x", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedBindingAsync(databasePath, "hermes_profile", "r2", "agent-b", null, "proj-x", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(AgentIdentity: "agent-a");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Single(result.Groups);
            Assert.Equal("agent-a", result.Groups[0].AgentIdentity);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task FiltersByRole()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "r1", "agent-a", null, "proj-x", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedBindingAsync(databasePath, "hermes_profile", "r2", "agent-b", null, "proj-x", "reviewer",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(Role: "runner");
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Single(result.Groups);
            Assert.Equal("runner", result.Groups[0].Role);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task EndpointReturns200WithValidStructure()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var database = new GatewayDatabase(databasePath);
            await database.InitializeAsync();

            await SeedBindingAsync(databasePath, "hermes_profile", "runner-1", "test-agent", null, "test-proj", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));
            await SeedDeliveryAsync(databasePath, "test-agent", "test-proj", "pending",
                dedupeKey: "dedupe:e2e", createdAt: TestNow.AddMinutes(-10));

            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DenGateway:Database:Path"] = databasePath,
                        ["DenGateway:DenCore:UseStub"] = "true",
                        ["DenGateway:DenChannels:UseStub"] = "true",
                        ["DenGateway:Sentinel:BindingTtlMinutes"] = "120"
                    }));
                });
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<GatewayStateOverviewResponse>(
                "/api/agent-overview/gateway-state?projectId=test-proj&agentIdentity=test-agent&role=runner");

            Assert.NotNull(response);
            Assert.Single(response.Groups);
            var group = response.Groups[0];
            Assert.Equal("test-proj", group.ProjectId);
            Assert.Equal("test-agent", group.AgentIdentity);
            Assert.Equal("runner", group.Role);
            Assert.NotNull(group.Binding);
            Assert.True(group.Binding.IsFresh);
            Assert.Equal(1, group.DeliveryCounts.Pending);
            Assert.NotNull(response.Metadata);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task MetadataReflectsQueryParameters()
    {
        var (databasePath, database) = CreateInitializedDatabase();
        try
        {
            await SeedBindingAsync(databasePath, "hermes_profile", "r1", "agent-a", null, "p1", "runner",
                "active", TestNow.AddMinutes(-5), TestNow.AddHours(2));

            var service = new GatewayStateOverviewService(database);
            var request = new GatewayStateOverviewRequest(
                ProjectId: "p1",
                AgentIdentity: "agent-a",
                Role: "runner",
                IncludeTerminalMinutes: 60,
                Limit: 50);
            var result = await service.GetGatewayStateOverviewAsync(request, TestNow);

            Assert.Equal(50, result.Metadata.Limit);
            Assert.Equal(60, result.Metadata.IncludeTerminalMinutes);
            Assert.Equal(1, result.Metadata.TotalGroups);
            Assert.Equal(1, result.Metadata.TotalBindings);
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    // --- Test helpers ---

    private static (string databasePath, GatewayDatabase database) CreateInitializedDatabase()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        database.InitializeAsync().GetAwaiter().GetResult();
        return (databasePath, database);
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "den-gateway-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "den-gateway.db");
    }

    private static void CleanupDatabase(string databasePath)
    {
        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
            var dir = Path.GetDirectoryName(databasePath);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static async Task SeedBindingAsync(
        string databasePath,
        string adapterKind,
        string adapterInstanceId,
        string agentIdentity,
        string? userIdentity,
        string? projectId,
        string? role,
        string status,
        DateTimeOffset lastSeenAt,
        DateTimeOffset? expiresAt)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gateway_adapter_bindings (
                adapter_kind, adapter_instance_id, agent_identity, user_identity, project_id, role,
                status, capabilities_json, metadata_json, last_seen_at, expires_at, created_at, updated_at
            ) VALUES (
                $kind, $instance_id, $agent, $user, $project, $role,
                $status, '{}', '{}', $last_seen, $expires, $last_seen, $last_seen
            );
            """;
        command.Parameters.AddWithValue("$kind", adapterKind);
        command.Parameters.AddWithValue("$instance_id", adapterInstanceId);
        command.Parameters.AddWithValue("$agent", DbValue(agentIdentity));
        command.Parameters.AddWithValue("$user", DbValue(userIdentity));
        command.Parameters.AddWithValue("$project", DbValue(projectId));
        command.Parameters.AddWithValue("$role", DbValue(role));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$last_seen", lastSeenAt.ToString("O"));
        command.Parameters.AddWithValue("$expires", expiresAt is null ? DBNull.Value : expiresAt.Value.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedDeliveryAsync(
        string databasePath,
        string targetIdentity,
        string? projectId,
        string status,
        string dedupeKey,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? leaseExpiresAt = null)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_requests (
                source_kind, target_type, target_identity, project_id, delivery_mode, priority,
                status, dedupe_key, attempt_count, cascade_depth, lease_expires_at, created_at, updated_at
            ) VALUES (
                'test', 'agent', $target, $project, 'notify', 3,
                $status, $dedupe, 0, 0, $lease, $created, $updated
            );
            """;
        command.Parameters.AddWithValue("$target", targetIdentity);
        command.Parameters.AddWithValue("$project", DbValue(projectId));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$dedupe", dedupeKey);
        command.Parameters.AddWithValue("$lease", leaseExpiresAt is null ? DBNull.Value : leaseExpiresAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", (updatedAt ?? createdAt).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static object DbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
