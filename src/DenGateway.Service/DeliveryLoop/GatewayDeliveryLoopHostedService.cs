using Microsoft.Extensions.Options;

namespace DenGateway.Service.DeliveryLoop;

public sealed class GatewayDeliveryLoopHostedService : BackgroundService
{
    private readonly GatewayDeliveryLoopService _deliveryLoop;
    private readonly GatewayChannelProjectDiscoveryService _projectDiscovery;
    private readonly IOptions<DenGatewayOptions> _options;
    private readonly ILogger<GatewayDeliveryLoopHostedService> _logger;

    public GatewayDeliveryLoopHostedService(
        GatewayDeliveryLoopService deliveryLoop,
        GatewayChannelProjectDiscoveryService projectDiscovery,
        IOptions<DenGatewayOptions> options,
        ILogger<GatewayDeliveryLoopHostedService> logger)
    {
        _deliveryLoop = deliveryLoop;
        _projectDiscovery = projectDiscovery;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value.DeliveryLoop;
        if (!options.Enabled)
        {
            _logger.LogInformation("Gateway delivery loop background poller disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        await PollSafelyAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PollSafelyAsync(stoppingToken);
        }
    }

    private async Task PollSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = _options.Value.DeliveryLoop;
            foreach (var request in await BuildPollRequestsAsync(options, cancellationToken))
            {
                var result = await _deliveryLoop.PollOnceAsync(request, cancellationToken);

                if (result.Status == "degraded" || result.Status == "rejected")
                {
                    _logger.LogWarning(
                        "Gateway delivery loop poll returned {Status} for {Source}/{ProjectId}: {ErrorCode} {Message}",
                        result.Status,
                        request.Source,
                        request.ProjectId ?? "*",
                        result.ErrorCode,
                        result.Message);
                    continue;
                }

                if (result.CreatedCount > 0 || result.SuppressedCount > 0 || result.DuplicateCount > 0)
                {
                    _logger.LogInformation(
                        "Gateway delivery loop poll completed for {Source}/{ProjectId}: seen={SeenCount}, created={CreatedCount}, duplicates={DuplicateCount}, suppressed={SuppressedCount}, nextCursor={NextCursor}",
                        request.Source,
                        request.ProjectId ?? "*",
                        result.SeenCount,
                        result.CreatedCount,
                        result.DuplicateCount,
                        result.SuppressedCount,
                        result.NextCursor);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway delivery loop poll failed.");
        }
    }

    private async Task<IReadOnlyList<GatewayDeliveryPollRequest>> BuildPollRequestsAsync(DeliveryLoopOptions options, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var configuredProject = string.IsNullOrWhiteSpace(options.ProjectId) ? null : options.ProjectId;
        var configuredProjects = options.ProjectIds
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Select(projectId => projectId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(configuredProject))
        {
            return [new GatewayDeliveryPollRequest(options.Source, configuredProject, options.Limit, now, options.SeedNewProjectCursorsAtLatest)];
        }

        if (!ChannelDiscoveryApplies(options.Source, options.DiscoverProjects) && configuredProjects.Length == 0)
        {
            return [new GatewayDeliveryPollRequest(options.Source, null, options.Limit, now)];
        }

        var channelProjects = configuredProjects;
        if (ChannelDiscoveryApplies(options.Source, options.DiscoverProjects))
        {
            channelProjects = (await _projectDiscovery.DiscoverAsync(cancellationToken))
                .Select(project => project.ProjectId)
                .ToArray();
        }

        if (string.Equals(options.Source, "channels", StringComparison.OrdinalIgnoreCase))
        {
            return channelProjects.Select(projectId => new GatewayDeliveryPollRequest("channels", projectId, options.Limit, now, options.SeedNewProjectCursorsAtLatest)).ToArray();
        }

        if (string.Equals(options.Source, "all", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { new GatewayDeliveryPollRequest("core", null, options.Limit, now) }
                .Concat(channelProjects.Select(projectId => new GatewayDeliveryPollRequest("channels", projectId, options.Limit, now, options.SeedNewProjectCursorsAtLatest)))
                .ToArray();
        }

        return [new GatewayDeliveryPollRequest(options.Source, null, options.Limit, now)];
    }

    private static bool ChannelDiscoveryApplies(string source, bool discoverProjects)
    {
        return discoverProjects && (string.Equals(source, "channels", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "all", StringComparison.OrdinalIgnoreCase));
    }
}
