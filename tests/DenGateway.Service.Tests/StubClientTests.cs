using DenGateway.Service.Clients;

namespace DenGateway.Service.Tests;

public class StubClientTests
{
    [Fact]
    public async Task StubDenCoreClientReturnsReadyHealthAndConfiguredBindings()
    {
        var binding = new GatewayBindingSnapshot(
            AdapterKind: "test",
            AdapterInstanceId: "adapter-1",
            AgentIdentity: "den-gateway-runner",
            UserIdentity: null,
            ProjectId: "den-gateway",
            Role: "runner",
            Status: "active",
            LastSeenAt: DateTimeOffset.Parse("2026-05-11T12:00:00Z"),
            ExpiresAt: DateTimeOffset.Parse("2026-05-11T13:00:00Z"),
            Metadata: new Dictionary<string, string> { ["transport"] = "test" });
        var client = new StubDenCoreClient([binding]);

        var health = await client.GetHealthAsync();
        var bindings = await client.ListActiveBindingsAsync();

        Assert.True(health.IsAvailable);
        Assert.Equal("stub", health.Mode);
        var single = Assert.Single(bindings.Items);
        Assert.Equal("den-gateway-runner", single.AgentIdentity);
        Assert.Equal("test", single.AdapterKind);
    }

    [Fact]
    public async Task StubDenCoreClientReportsUnavailableForMissingUpstreamContracts()
    {
        var client = new StubDenCoreClient([]);

        var sourceSummary = await client.GetSourceSummaryAsync("task_message", "123", "den-gateway");
        var events = await client.ReadEventOutboxAsync(after: null, projectId: "den-gateway", limit: 100);
        var reconciliation = await client.PostGatewayReconciliationEventsAsync([
            new GatewayReconciliationEvent("pause_sent", "den-gateway-runner", "{}", DateTimeOffset.Parse("2026-05-11T12:00:00Z"))
        ]);

        Assert.False(sourceSummary.IsAvailable);
        Assert.Equal("not_implemented", sourceSummary.ErrorCode);
        Assert.False(events.IsAvailable);
        Assert.Equal("not_implemented", events.ErrorCode);
        Assert.False(reconciliation.IsAvailable);
        Assert.Equal("not_implemented", reconciliation.ErrorCode);
    }

    [Fact]
    public async Task StubDenChannelsClientReturnsConfiguredMembershipsAndUnavailableEventCursor()
    {
        var membership = new ChannelMembershipSnapshot(
            ChannelId: "channel-1",
            MemberType: "agent",
            MemberIdentity: "den-gateway-runner",
            WakePolicy: "mentions_only",
            Status: "active",
            CooldownSeconds: 60,
            Settings: new Dictionary<string, string>());
        var client = new StubDenChannelsClient([membership]);

        var health = await client.GetHealthAsync();
        var memberships = await client.ListMembershipsAsync("channel-1");
        var events = await client.ReadChannelEventsAsync(after: null, projectId: "den-gateway", channelId: null, limit: 100);

        Assert.True(health.IsAvailable);
        Assert.Equal("stub", health.Mode);
        var single = Assert.Single(memberships.Items);
        Assert.Equal("mentions_only", single.WakePolicy);
        Assert.False(events.IsAvailable);
        Assert.Equal("not_implemented", events.ErrorCode);
    }
}
