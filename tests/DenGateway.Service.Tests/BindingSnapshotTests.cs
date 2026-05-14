using System.Net.Http.Json;
using DenGateway.Service.Bindings;
using DenGateway.Service.Clients;
using DenGateway.Service.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenGateway.Service.Tests;

public class BindingSnapshotTests
{
    [Fact]
    public async Task RefreshFromCoreCachesInspectableFreshAndStaleBindingSnapshots()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var core = new RecordingCoreClient
        {
            Bindings =
            [
                new GatewayBindingSnapshot("hermes_profile", "runner-main", "den-gateway-runner", null, "den-gateway", "runner", "active", DateTimeOffset.Parse("2026-05-14T04:20:00Z"), null, new Dictionary<string, string> { ["sessionId"] = "s1" }),
                new GatewayBindingSnapshot("hermes_profile", "stale-main", "stale-agent", null, "den-gateway", "runner", "active", DateTimeOffset.Parse("2026-05-14T01:00:00Z"), null, new Dictionary<string, string>())
            ]
        };
        var service = new BindingSnapshotService(database, core, new BindingSnapshotSettings(BindingTtlMinutes: 120));

        var result = await service.RefreshAsync(DateTimeOffset.Parse("2026-05-14T04:30:00Z"));
        var health = await service.GetHealthAsync(DateTimeOffset.Parse("2026-05-14T04:30:00Z"));
        var snapshots = await service.ListAsync(DateTimeOffset.Parse("2026-05-14T04:30:00Z"));

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.RefreshedCount);
        Assert.Equal("degraded", health.Status);
        Assert.Equal(1, health.FreshCount);
        Assert.Equal(1, health.StaleCount);
        Assert.Collection(snapshots.OrderBy(s => s.AgentIdentity),
            stale =>
            {
                Assert.Equal("den-gateway-runner", stale.AgentIdentity);
                Assert.False(stale.IsStale);
            },
            stale =>
            {
                Assert.Equal("stale-agent", stale.AgentIdentity);
                Assert.True(stale.IsStale);
                Assert.Equal("ttl_expired", stale.StalenessReason);
            });
    }

    [Fact]
    public async Task RepeatedDegradedAndRecoveryTransitionsPostOneCoreReconciliationEventEach()
    {
        var databasePath = CreateTempDatabasePath();
        var database = new GatewayDatabase(databasePath);
        await database.InitializeAsync();
        var core = new RecordingCoreClient();
        var service = new BindingSnapshotService(database, core, new BindingSnapshotSettings(BindingTtlMinutes: 120));

        await service.RecordVisibleHealthTransitionAsync("degraded", "binding_stale", DateTimeOffset.Parse("2026-05-14T04:40:00Z"));
        await service.RecordVisibleHealthTransitionAsync("degraded", "binding_stale", DateTimeOffset.Parse("2026-05-14T04:41:00Z"));
        await service.RecordVisibleHealthTransitionAsync("recovered", "bindings_fresh", DateTimeOffset.Parse("2026-05-14T04:50:00Z"));
        await service.RecordVisibleHealthTransitionAsync("recovered", "bindings_fresh", DateTimeOffset.Parse("2026-05-14T04:51:00Z"));

        Assert.Equal(2, core.PostedEvents.Count);
        Assert.Equal("binding_health_degraded", core.PostedEvents[0].EventKind);
        Assert.Equal("binding_health_recovered", core.PostedEvents[1].EventKind);
    }

    [Fact]
    public async Task BindingSnapshotEndpointsExposeRefreshListAndGatewayStatusHealth()
    {
        var databasePath = CreateTempDatabasePath();
        var core = new RecordingCoreClient
        {
            Bindings =
            [
                new GatewayBindingSnapshot("hermes_profile", "runner-main", "den-gateway-runner", null, "den-gateway", "runner", "active", DateTimeOffset.Parse("2026-05-14T04:20:00Z"), null, new Dictionary<string, string>())
            ]
        };
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
                builder.ConfigureServices(services => services.AddSingleton<IDenCoreClient>(core));
            });
        using var client = factory.CreateClient();

        var refresh = await client.PostAsJsonAsync("/api/binding-snapshots/refresh", new { now = "2026-05-14T04:30:00Z" });
        refresh.EnsureSuccessStatusCode();
        var list = await client.GetFromJsonAsync<BindingSnapshotListResponse>("/api/binding-snapshots?now=2026-05-14T04%3A30%3A00Z");
        var status = await client.GetFromJsonAsync<GatewayStatusWithBindings>("/api/gateway/status?now=2026-05-14T04%3A30%3A00Z");

        Assert.NotNull(list);
        Assert.Single(list.Items);
        Assert.False(list.Items[0].IsStale);
        Assert.NotNull(status);
        Assert.Equal("available", status.Bindings.Status);
        Assert.Equal(1, status.Bindings.FreshCount);
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "den-gateway-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "den-gateway.db");
    }

    private sealed class RecordingCoreClient : IDenCoreClient
    {
        public IReadOnlyList<GatewayBindingSnapshot> Bindings { get; init; } = [];
        public List<GatewayReconciliationEvent> PostedEvents { get; } = [];
        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));
        public Task<ClientListResult<GatewayBindingSnapshot>> ListActiveBindingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<GatewayBindingSnapshot>.Available(Bindings));
        public Task<ClientValueResult<SourceSummary>> GetSourceSummaryAsync(string sourceKind, string sourceId, string? projectId, CancellationToken cancellationToken = default) => Task.FromResult(ClientValueResult<SourceSummary>.Unavailable("not_found", "missing"));
        public Task<ClientListResult<GatewayOutboxEvent>> ReadEventOutboxAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<GatewayOutboxEvent>.Available([]));
        public Task<ClientOperationResult> PostGatewayReconciliationEventsAsync(IReadOnlyList<GatewayReconciliationEvent> events, CancellationToken cancellationToken = default)
        {
            PostedEvents.AddRange(events);
            return Task.FromResult(ClientOperationResult.Completed("ok"));
        }
    }

    private sealed record BindingSnapshotListResponse(IReadOnlyList<BindingSnapshotDto> Items);
    private sealed record BindingSnapshotDto(string? AgentIdentity, bool IsStale, string? StalenessReason);
    private sealed record GatewayStatusWithBindings(BindingSnapshotHealth Bindings);
}
