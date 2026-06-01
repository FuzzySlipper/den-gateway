using DenGateway.Service.NotificationMirror;
using Microsoft.Extensions.Options;

namespace DenGateway.Service.DeliveryLoop;

public sealed class GatewayDeliveryLoopHostedService : BackgroundService
{
    private readonly GatewayDeliveryLoopService _deliveryLoop;
    private readonly GatewayChannelProjectDiscoveryService _projectDiscovery;
    private readonly GatewayNotificationMirrorService? _notificationMirror;
    private readonly IOptions<DenGatewayOptions> _options;
    private readonly ILogger<GatewayDeliveryLoopHostedService> _logger;

    public GatewayDeliveryLoopHostedService(
        GatewayDeliveryLoopService deliveryLoop,
        GatewayChannelProjectDiscoveryService projectDiscovery,
        GatewayNotificationMirrorService? notificationMirror,
        IOptions<DenGatewayOptions> options,
        ILogger<GatewayDeliveryLoopHostedService> logger)
    {
        _deliveryLoop = deliveryLoop;
        _projectDiscovery = projectDiscovery;
        _notificationMirror = notificationMirror;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deliveryOptions = _options.Value.DeliveryLoop;
        var mirrorOptions = _options.Value.NotificationLaneMirror;
        if (!deliveryOptions.Enabled && !mirrorOptions.Enabled)
        {
            _logger.LogInformation("Gateway delivery loop and notification mirror background pollers disabled by configuration.");
            return;
        }

        var pollSeconds = deliveryOptions.Enabled
            ? deliveryOptions.PollIntervalSeconds
            : mirrorOptions.PollIntervalSeconds;
        var interval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
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
            if (options.Enabled)
            {
                foreach (var request in await BuildPollRequestsAsync(options, cancellationToken))
                {
                    var result = await _deliveryLoop.PollOnceAsync(request, cancellationToken);

                    if (result.Status == "degraded" || result.Status == "rejected")
                    {
                        _logger.LogWarning(
                            "Gateway delivery loop poll returned {Status} for {Source}/{ProjectId}/{ChannelId}: {ErrorCode} {Message}",
                            result.Status,
                            request.Source,
                            request.ProjectId ?? "*",
                            request.GetChannelId() ?? "*",
                            result.ErrorCode,
                            result.Message);
                        continue;
                    }

                    if (result.CreatedCount > 0 || result.SuppressedCount > 0 || result.DuplicateCount > 0)
                    {
                        _logger.LogInformation(
                            "Gateway delivery loop poll completed for {Source}/{ProjectId}/{ChannelId}: seen={SeenCount}, created={CreatedCount}, duplicates={DuplicateCount}, suppressed={SuppressedCount}, nextCursor={NextCursor}",
                            request.Source,
                            request.ProjectId ?? "*",
                            request.GetChannelId() ?? "*",
                            result.SeenCount,
                            result.CreatedCount,
                            result.DuplicateCount,
                            result.SuppressedCount,
                            result.NextCursor);
                    }
                }
            }

            // Poll notification mirror if available
            if (_notificationMirror is not null && _options.Value.NotificationLaneMirror.Enabled)
            {
                var mirrorResult = await _notificationMirror.PollAndMirrorOnceAsync(cancellationToken);
                if (mirrorResult.Status == "degraded")
                {
                    _logger.LogWarning(
                        "Gateway notification mirror poll degraded: {ErrorCode} {Message}",
                        mirrorResult.ErrorCode,
                        mirrorResult.Message);
                }
                else if (mirrorResult.MirroredCount > 0 || mirrorResult.SkippedCount > 0)
                {
                    _logger.LogInformation(
                        "Gateway notification mirror poll completed: mirrored={MirroredCount}, duplicates={DuplicateCount}, skipped={SkippedCount}",
                        mirrorResult.MirroredCount,
                        mirrorResult.DuplicateCount,
                        mirrorResult.SkippedCount);
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
        var configuredChannels = options.ChannelIds
            .Where(channelId => !string.IsNullOrWhiteSpace(channelId))
            .Select(channelId => channelId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(configuredProject))
        {
            return [new GatewayDeliveryPollRequest(options.Source, configuredProject, options.Limit, now, options.SeedNewProjectCursorsAtLatest)];
        }

        if (!ChannelDiscoveryApplies(options.Source, options.DiscoverProjects) && configuredProjects.Length == 0 && configuredChannels.Length == 0)
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

        var channelPolls = configuredChannels
            .Select(channelId => new GatewayDeliveryPollRequest("channels", null, options.Limit, now, SeedCursorAtLatestWhenMissing: false, ChannelId: channelId))
            .ToArray();

        if (string.Equals(options.Source, "channels", StringComparison.OrdinalIgnoreCase))
        {
            return channelProjects.Select(projectId => new GatewayDeliveryPollRequest("channels", projectId, options.Limit, now, options.SeedNewProjectCursorsAtLatest))
                .Concat(channelPolls)
                .ToArray();
        }

        if (string.Equals(options.Source, "all", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { new GatewayDeliveryPollRequest("core", null, options.Limit, now) }
                .Concat(channelProjects.Select(projectId => new GatewayDeliveryPollRequest("channels", projectId, options.Limit, now, options.SeedNewProjectCursorsAtLatest)))
                .Concat(channelPolls)
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
