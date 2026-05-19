using DenGateway.Service.Clients;
using DenGateway.Service.DeliveryLoop;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DenGateway.Service.Tests;

public class GatewayChannelProjectDiscoveryTests
{
    [Fact]
    public async Task DiscoveryFindsDenNetworkStyleProjectWithoutManualProjectIds()
    {
        var core = new FakeCoreClient
        {
            Projects =
            [
                new DenProjectSnapshot("den-channels", "Den Channels", "project", "normal"),
                new DenProjectSnapshot("den-gateway", "Den Gateway", "project", "normal"),
                new DenProjectSnapshot("den-network", "Den Network", "project", "normal")
            ]
        };
        var channels = new FakeChannelsClient
        {
            MembershipsByProject = new Dictionary<string, ChannelMembershipListSnapshot>
            {
                ["den-network"] = ProjectMemberships("den-network", "8", new ChannelMembershipSnapshot("8", "agent", "sysadmin", "all_human_messages", "active", 60, new Dictionary<string, string> { ["projectId"] = "den-network" }))
            }
        };
        var service = NewService(core, channels, new DeliveryLoopOptions
        {
            DiscoverProjects = true,
            ProjectIds = ["den-channels", "den-gateway", "den-hermes-bridge"]
        });

        var discovered = await service.DiscoverAsync();

        Assert.Contains(discovered, project => project.ProjectId == "den-network" && project.ChannelId == "8");
    }

    [Fact]
    public async Task DiscoveryHonorsExcludeList()
    {
        var core = new FakeCoreClient
        {
            Projects = [new DenProjectSnapshot("den-network", "Den Network", "project", "normal")]
        };
        var channels = new FakeChannelsClient
        {
            MembershipsByProject = new Dictionary<string, ChannelMembershipListSnapshot>
            {
                ["den-network"] = ProjectMemberships("den-network", "8", new ChannelMembershipSnapshot("8", "agent", "sysadmin", "wake", "active", 60, new Dictionary<string, string>()))
            }
        };
        var service = NewService(core, channels, new DeliveryLoopOptions
        {
            DiscoverProjects = true,
            ExcludedProjectIds = ["den-network"]
        });

        var discovered = await service.DiscoverAsync();

        Assert.DoesNotContain(discovered, project => project.ProjectId == "den-network");
    }

    [Fact]
    public async Task DiscoveryFiltersArchivedAndProjectsWithoutWakeRelevantAgentMemberships()
    {
        var core = new FakeCoreClient
        {
            Projects =
            [
                new DenProjectSnapshot("archived-project", "Archived", "project", "archived"),
                new DenProjectSnapshot("quiet-project", "Quiet", "project", "normal"),
                new DenProjectSnapshot("valid-project", "Valid", "project", "normal")
            ]
        };
        var channels = new FakeChannelsClient
        {
            MembershipsByProject = new Dictionary<string, ChannelMembershipListSnapshot>
            {
                ["archived-project"] = ProjectMemberships("archived-project", "1", new ChannelMembershipSnapshot("1", "agent", "archived-agent", "wake", "active", 60, new Dictionary<string, string>())),
                ["quiet-project"] = ProjectMemberships("quiet-project", "2", new ChannelMembershipSnapshot("2", "agent", "quiet-agent", "record_only", "active", 60, new Dictionary<string, string>())),
                ["valid-project"] = ProjectMemberships("valid-project", "3", new ChannelMembershipSnapshot("3", "agent", "runner", "mentions_only", "active", 60, new Dictionary<string, string>()))
            }
        };
        var service = NewService(core, channels, new DeliveryLoopOptions { DiscoverProjects = true });

        var discovered = await service.DiscoverAsync();

        Assert.Equal(["valid-project"], discovered.Select(project => project.ProjectId).ToArray());
    }

    private static GatewayChannelProjectDiscoveryService NewService(FakeCoreClient core, FakeChannelsClient channels, DeliveryLoopOptions options)
    {
        return new GatewayChannelProjectDiscoveryService(
            core,
            channels,
            Options.Create(new DenGatewayOptions { DeliveryLoop = options }),
            NullLogger<GatewayChannelProjectDiscoveryService>.Instance);
    }

    private static ChannelMembershipListSnapshot ProjectMemberships(string projectId, string channelId, params ChannelMembershipSnapshot[] members)
    {
        return new ChannelMembershipListSnapshot(channelId, $"project-{projectId}", "project_default", projectId, members);
    }

    private sealed class FakeCoreClient : IDenCoreClient
    {
        public IReadOnlyList<DenProjectSnapshot> Projects { get; init; } = [];
        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));
        public Task<ClientListResult<DenProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<DenProjectSnapshot>.Available(Projects));
        public Task<ClientListResult<GatewayBindingSnapshot>> ListActiveBindingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<GatewayBindingSnapshot>.Available([]));
        public Task<ClientValueResult<SourceSummary>> GetSourceSummaryAsync(string sourceKind, string sourceId, string? projectId, CancellationToken cancellationToken = default) => Task.FromResult(ClientValueResult<SourceSummary>.Unavailable("not_found", "missing"));
        public Task<ClientListResult<GatewayOutboxEvent>> ReadEventOutboxAsync(string? after, string? projectId, int limit, CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<GatewayOutboxEvent>.Available([]));
        public Task<ClientOperationResult> PostGatewayReconciliationEventsAsync(IReadOnlyList<GatewayReconciliationEvent> events, CancellationToken cancellationToken = default) => Task.FromResult(ClientOperationResult.Completed("ok"));
    }

    private sealed class FakeChannelsClient : IDenChannelsClient
    {
        public IReadOnlyDictionary<string, ChannelMembershipListSnapshot> MembershipsByProject { get; init; } = new Dictionary<string, ChannelMembershipListSnapshot>();
        public Task<ServiceHealthResult> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServiceHealthResult.Available("fake", "ok"));
        public Task<ClientValueResult<ChannelMembershipListSnapshot>> ListProjectMembershipsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(MembershipsByProject.TryGetValue(projectId, out var memberships) ? ClientValueResult<ChannelMembershipListSnapshot>.Available(memberships) : ClientValueResult<ChannelMembershipListSnapshot>.Unavailable("not_found", "missing"));
        public Task<ClientValueResult<ChannelMessageSnapshot>> GetChannelMessageAsync(string channelMessageId, CancellationToken cancellationToken = default) => Task.FromResult(ClientValueResult<ChannelMessageSnapshot>.Unavailable("not_found", "missing"));
        public Task<ClientListResult<ChannelMembershipSnapshot>> ListMembershipsAsync(string channelId, CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<ChannelMembershipSnapshot>.Available([]));
        public Task<ClientOperationResult> PostMirrorOrSystemMessageAsync(ChannelMirrorMessage message, CancellationToken cancellationToken = default) => Task.FromResult(ClientOperationResult.Completed("ok"));
        public Task<ChannelActivityPostResult> PostActivityEventAsync(ChannelActivityEventWrite activityEvent, CancellationToken cancellationToken = default) => Task.FromResult(ChannelActivityPostResult.Completed("1", "ok"));
        public Task<ClientListResult<ChannelEventSnapshot>> ReadChannelEventsAsync(string? after, string? projectId, string? channelId, int limit, CancellationToken cancellationToken = default) => Task.FromResult(ClientListResult<ChannelEventSnapshot>.Available([]));
        public Task<ClientValueResult<string>> GetLatestChannelEventCursorAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(ClientValueResult<string>.Unavailable("empty_cursor", "no events"));
    }
}
