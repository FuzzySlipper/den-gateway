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

    private sealed record LiveResponse(string Status, string Service);
    private sealed record ReadyResponse(string Status, Dictionary<string, object> Checks);
    private sealed record GatewayStatus(string Service, string Status, string DatabasePath, string DenCoreMode, string DenChannelsMode, SentinelStatus Sentinel);
    private sealed record SentinelStatus(string SentinelId, string State, int PollIntervalSeconds, int BindingTtlMinutes);
}
