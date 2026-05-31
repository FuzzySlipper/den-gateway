using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DenGateway.Service.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LiveEndpointReturnsLiveStatus()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LiveResponse>();
        Assert.NotNull(body);
        Assert.Equal("live", body.Status);
        Assert.Equal("den-gateway", body.Service);
    }

    [Fact]
    public async Task ReadyEndpointReportsConfiguredStubDependencies()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReadyResponse>();
        Assert.NotNull(body);
        Assert.Equal("ready", body.Status);
        Assert.True(body.Checks.ContainsKey("database"));
        Assert.True(body.Checks.ContainsKey("denCore"));
        Assert.True(body.Checks.ContainsKey("denChannels"));
        Assert.True(body.Checks.ContainsKey("bindings"));
    }

    [Fact]
    public async Task GatewayStatusUsesConfiguredDefaults()
    {
        using var client = _factory.CreateClient();

        var status = await client.GetFromJsonAsync<GatewayStatus>("/api/gateway/status");

        Assert.NotNull(status);
        Assert.Equal("den-gateway", status.Service);
        Assert.Equal("ready", status.Status);
        Assert.Equal("data/den-gateway.db", status.DatabasePath);
        Assert.Equal("stub", status.DenCoreMode);
        Assert.Equal("stub", status.DenChannelsMode);
        Assert.Equal("den-k8-sentinel-1", status.Sentinel.SentinelId);
        Assert.Equal("normal", status.Sentinel.State);
    }

    [Fact]
    public async Task SentinelStatusEndpointReportsInitialNormalState()
    {
        using var client = _factory.CreateClient();

        var status = await client.GetFromJsonAsync<SentinelStatusEndpointResponse>("/api/sentinel/status");

        Assert.NotNull(status);
        Assert.Equal("den-k8-sentinel-1", status.SentinelId);
        Assert.Equal("normal", status.State);
        Assert.Equal(10, status.PollIntervalSeconds);
        Assert.Equal("unknown", status.Bindings.Status);
    }

    private sealed record LiveResponse(string Status, string Service);
    private sealed record ReadyResponse(string Status, Dictionary<string, object> Checks);
    private sealed record GatewayStatus(string Service, string Status, string DatabasePath, string DenCoreMode, string DenChannelsMode, SentinelStatus Sentinel);
    private sealed record SentinelStatus(string SentinelId, string State, int PollIntervalSeconds, int BindingTtlMinutes);
    private sealed record SentinelStatusEndpointResponse(string SentinelId, string State, int PollIntervalSeconds, int DegradedFailureThreshold, int DownFailureThreshold, int StableSuccessThreshold, TestBindingHealth Bindings);
    private sealed record TestBindingHealth(string Status);

    [Fact]
    public async Task FleetOpsOverviewEndpoint_ReturnsExpectedShape()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/gateway/fleet-ops");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FleetOpsOverviewShape>();

        Assert.NotNull(body);
        Assert.Equal("den-gateway", body.Service);
        Assert.NotEqual(default, body.GeneratedAt);
        Assert.NotNull(body.ServiceUnits);
        Assert.NotNull(body.Actions);
        Assert.NotEmpty(body.Actions);

        // Verify expected action IDs are present
        var actionIds = body.Actions.Select(a => a.ActionId).ToList();
        Assert.Contains("fleet-status", actionIds);
        Assert.Contains("restart-all", actionIds);
        Assert.Contains("restart-profile", actionIds);

        // Verify restart-profile has a profile arg schema
        var restartProfile = body.Actions.First(a => a.ActionId == "restart-profile");
        Assert.True(restartProfile.Mutating);
        Assert.NotNull(restartProfile.ArgsSchema);
        Assert.Single(restartProfile.ArgsSchema);
        Assert.Equal("profile", restartProfile.ArgsSchema![0].Name);
        Assert.Equal("^[a-zA-Z0-9_-]+$", restartProfile.ArgsSchema[0].Pattern);
    }

    [Fact]
    public async Task FleetOpsRunActionEndpoint_RejectsUnknownAction()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/gateway/fleet-ops/actions/nonexistent/runs",
            new { actionId = "nonexistent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FleetOpsRunShape>();
        Assert.NotNull(body);
        Assert.Equal("failed", body.Status);
        Assert.NotNull(body.ErrorMessage);
    }

    [Fact]
    public async Task FleetOpsGetRunEndpoint_ReturnsNotFoundForUnknownRun()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/gateway/fleet-ops/runs/nonexistent-run-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record FleetOpsOverviewShape(
        string Service,
        DateTimeOffset GeneratedAt,
        List<FleetOpsUnitShape> ServiceUnits,
        List<FleetOpsActionShape> Actions,
        string? DiscoveryDiagnostics);
    private sealed record FleetOpsUnitShape(string UnitName, string ProfileName, string ActiveState, string SubState);
    private sealed record FleetOpsActionShape(
        string ActionId,
        string Label,
        string RiskLevel,
        bool Mutating,
        bool SupportsDryRun,
        bool NeedsConfirmation,
        List<FleetOpsArgSchemaShape>? ArgsSchema);
    private sealed record FleetOpsArgSchemaShape(string Name, string Type, bool Required, string Description, string? Pattern);
    private sealed record FleetOpsRunShape(string RunId, string ActionId, string Status, string? ErrorMessage);
}
