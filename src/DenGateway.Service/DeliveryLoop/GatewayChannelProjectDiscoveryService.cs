using DenGateway.Service.Clients;
using Microsoft.Extensions.Options;

namespace DenGateway.Service.DeliveryLoop;

public sealed class GatewayChannelProjectDiscoveryService
{
    private static readonly HashSet<string> WakeRelevantPolicies = new(StringComparer.OrdinalIgnoreCase)
    {
        "wake",
        "notify",
        "all_human_messages",
        "all_messages_except_self",
        "mentions_only",
        "direct_questions_only",
        "substantive_digest"
    };

    private readonly IDenCoreClient _denCoreClient;
    private readonly IDenChannelsClient _denChannelsClient;
    private readonly IOptions<DenGatewayOptions> _options;
    private readonly ILogger<GatewayChannelProjectDiscoveryService> _logger;

    public GatewayChannelProjectDiscoveryService(
        IDenCoreClient denCoreClient,
        IDenChannelsClient denChannelsClient,
        IOptions<DenGatewayOptions> options,
        ILogger<GatewayChannelProjectDiscoveryService> logger)
    {
        _denCoreClient = denCoreClient;
        _denChannelsClient = denChannelsClient;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiscoveredChannelProject>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value.DeliveryLoop;
        if (!options.DiscoverProjects)
        {
            return NormalizeManualProjects(options.ProjectIds)
                .Select(projectId => new DiscoveredChannelProject(projectId, null, "manual_config"))
                .ToArray();
        }

        var excluded = new HashSet<string>(
            options.ExcludedProjectIds.Where(projectId => !string.IsNullOrWhiteSpace(projectId)).Select(projectId => projectId.Trim()),
            StringComparer.Ordinal);
        var projects = await _denCoreClient.ListProjectsAsync(cancellationToken);
        if (!projects.IsAvailable)
        {
            _logger.LogWarning(
                "Gateway delivery-loop project discovery could not list Den Core projects: {ErrorCode} {Message}",
                projects.ErrorCode,
                projects.Message);
            return [];
        }

        var discovered = new List<DiscoveredChannelProject>();
        foreach (var project in projects.Items)
        {
            if (!IsNormalProject(project) || excluded.Contains(project.ProjectId))
            {
                continue;
            }

            var memberships = await _denChannelsClient.ListProjectMembershipsAsync(project.ProjectId, cancellationToken);
            if (!memberships.IsAvailable || memberships.Value is null)
            {
                continue;
            }

            var surface = memberships.Value;
            if (!string.Equals(surface.ChannelKind, "project_default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!surface.Members.Any(IsWakeRelevantActiveAgentMembership))
            {
                continue;
            }

            discovered.Add(new DiscoveredChannelProject(project.ProjectId, surface.ChannelId, "discovered"));
        }

        return discovered
            .GroupBy(project => project.ProjectId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(project => project.ProjectId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsNormalProject(DenProjectSnapshot project)
    {
        return string.Equals(project.Kind, "project", StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.Visibility, "normal", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(project.ProjectId);
    }

    private static bool IsWakeRelevantActiveAgentMembership(ChannelMembershipSnapshot membership)
    {
        return string.Equals(membership.MemberType, "agent", StringComparison.OrdinalIgnoreCase)
            && string.Equals(membership.Status, "active", StringComparison.OrdinalIgnoreCase)
            && WakeRelevantPolicies.Contains(membership.WakePolicy);
    }

    private static IReadOnlyList<string> NormalizeManualProjects(IEnumerable<string> projectIds)
    {
        return projectIds
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Select(projectId => projectId.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(projectId => projectId, StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed record DiscoveredChannelProject(string ProjectId, string? ChannelId, string Source);
