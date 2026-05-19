using DenGateway.Service.Clients;

namespace DenGateway.Service.Activity;

public sealed class ChannelActivityEventRouter
{
    private const int MaxDiagnostics = 20;
    private readonly IDenChannelsClient _denChannelsClient;
    private readonly ILogger<ChannelActivityEventRouter> _logger;
    private readonly Queue<ChannelActivityDiagnostic> _recentDiagnostics = new();
    private readonly object _sync = new();

    public ChannelActivityEventRouter(IDenChannelsClient denChannelsClient, ILogger<ChannelActivityEventRouter> logger)
    {
        _denChannelsClient = denChannelsClient;
        _logger = logger;
    }

    public async Task<ChannelActivityRouteResult> RouteAsync(GatewayChannelActivityEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            return new ChannelActivityRouteResult("rejected", false, null, "missing_channel_id", "channelId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.AgentIdentity))
        {
            return new ChannelActivityRouteResult("rejected", false, null, "missing_agent_identity", "agentIdentity is required.");
        }

        var write = new ChannelActivityEventWrite(
            ChannelId: request.ChannelId,
            ProjectId: request.ProjectId,
            AgentIdentity: request.AgentIdentity,
            DeliveryRequestId: request.DeliveryRequestId,
            HermesSessionKey: request.HermesSessionKey,
            TaskId: request.TaskId,
            ThreadId: request.ThreadId,
            AnchorMessageId: request.AnchorMessageId,
            EventType: string.IsNullOrWhiteSpace(request.EventType) ? "lifecycle_status" : request.EventType,
            Status: string.IsNullOrWhiteSpace(request.Status) ? "interim" : request.Status,
            Sequence: request.Sequence,
            Title: request.Title,
            Summary: request.Summary,
            PreviewJson: request.PreviewJson,
            MetadataJson: request.MetadataJson,
            DedupeKey: request.DedupeKey);

        ChannelActivityPostResult result;
        try
        {
            result = await _denChannelsClient.PostActivityEventAsync(write, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var exceptionDiagnostic = BuildDiagnostic(request, "activity_record_exception", ex.Message);
            RecordDiagnostic(exceptionDiagnostic);
            _logger.LogWarning(ex,
                "Den Channels activity event write threw for channel {ChannelId}, delivery {DeliveryRequestId}.",
                exceptionDiagnostic.ChannelId,
                exceptionDiagnostic.DeliveryRequestId);
            return new ChannelActivityRouteResult("degraded", false, null, exceptionDiagnostic.ErrorCode, exceptionDiagnostic.Message);
        }

        if (result.IsAvailable)
        {
            return new ChannelActivityRouteResult("recorded", true, result.ActivityEventId, null, result.Message);
        }

        var diagnostic = BuildDiagnostic(
            request,
            result.ErrorCode ?? "activity_record_failed",
            result.Message ?? "Den Channels activity event write failed.");
        RecordDiagnostic(diagnostic);
        _logger.LogWarning(
            "Den Channels activity event write failed for channel {ChannelId}, delivery {DeliveryRequestId}: {ErrorCode} {Message}",
            diagnostic.ChannelId,
            diagnostic.DeliveryRequestId,
            diagnostic.ErrorCode,
            diagnostic.Message);

        return new ChannelActivityRouteResult("degraded", false, null, diagnostic.ErrorCode, diagnostic.Message);
    }

    public ChannelActivityRouterStatus GetStatus()
    {
        lock (_sync)
        {
            return new ChannelActivityRouterStatus(_recentDiagnostics.ToArray());
        }
    }

    private static ChannelActivityDiagnostic BuildDiagnostic(GatewayChannelActivityEventRequest request, string errorCode, string message) => new(
        ObservedAt: DateTimeOffset.UtcNow,
        ChannelId: request.ChannelId,
        ProjectId: request.ProjectId,
        AgentIdentity: request.AgentIdentity,
        DeliveryRequestId: request.DeliveryRequestId,
        ErrorCode: errorCode,
        Message: message);

    private void RecordDiagnostic(ChannelActivityDiagnostic diagnostic)
    {
        lock (_sync)
        {
            _recentDiagnostics.Enqueue(diagnostic);
            while (_recentDiagnostics.Count > MaxDiagnostics)
            {
                _recentDiagnostics.Dequeue();
            }
        }
    }
}

public sealed record GatewayChannelActivityEventRequest(
    string ChannelId,
    string? ProjectId,
    string AgentIdentity,
    string? DeliveryRequestId,
    string? HermesSessionKey,
    long? TaskId,
    long? ThreadId,
    long? AnchorMessageId,
    string? EventType,
    string? Status,
    long? Sequence,
    string? Title,
    string? Summary,
    string? PreviewJson,
    string? MetadataJson,
    string? DedupeKey);

public sealed record ChannelActivityRouteResult(
    string Status,
    bool Recorded,
    string? ActivityEventId,
    string? ErrorCode,
    string? Message);

public sealed record ChannelActivityRouterStatus(IReadOnlyList<ChannelActivityDiagnostic> RecentFailures);

public sealed record ChannelActivityDiagnostic(
    DateTimeOffset ObservedAt,
    string ChannelId,
    string? ProjectId,
    string AgentIdentity,
    string? DeliveryRequestId,
    string ErrorCode,
    string Message);
