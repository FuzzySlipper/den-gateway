using System.Text.Json;
using System.Text.Json.Serialization;
using DenGateway.Service.Clients;
using DenGateway.Service.Persistence;
using Microsoft.Extensions.Options;

namespace DenGateway.Service.NotificationMirror;

public sealed class GatewayNotificationMirrorService
{
    private readonly GatewayDatabase _database;
    private readonly IDenCoreClient _denCoreClient;
    private readonly IDenChannelsClient _denChannelsClient;
    private readonly IOptions<DenGatewayOptions> _options;

    public GatewayNotificationMirrorService(
        GatewayDatabase database,
        IDenCoreClient denCoreClient,
        IDenChannelsClient denChannelsClient,
        IOptions<DenGatewayOptions> options)
    {
        _database = database;
        _denCoreClient = denCoreClient;
        _denChannelsClient = denChannelsClient;
        _options = options;
    }

    public async Task<NotificationMirrorPollResult> PollAndMirrorOnceAsync(CancellationToken cancellationToken = default)
    {
        var mirrorOptions = _options.Value.NotificationLaneMirror;
        if (!mirrorOptions.Enabled)
        {
            return new NotificationMirrorPollResult("disabled", 0, 0, 0, null, null);
        }

        if (string.IsNullOrWhiteSpace(mirrorOptions.TargetChannelId))
        {
            return new NotificationMirrorPollResult("degraded", 0, 0, 0, "missing_target_channel", "NotificationLaneMirror:TargetChannelId is not configured.");
        }

        var includedTypes = new HashSet<string>(mirrorOptions.IncludedMetadataTypes ?? [], StringComparer.OrdinalIgnoreCase);
        if (includedTypes.Count == 0)
        {
            return new NotificationMirrorPollResult("degraded", 0, 0, 0, "empty_type_filter", "NotificationLaneMirror:IncludedMetadataTypes is empty; no metadata types to mirror.");
        }

        var after = await _database.ReadDeliveryLoopCursorAsync("notification_mirror", null, cancellationToken);
        var highWaterId = ParseNotificationId(after);
        var limit = Math.Clamp(mirrorOptions.Limit, 1, 200);
        var notifications = await _denCoreClient.ListUserNotificationsAsync(limit: limit, projectId: null, after: after, cancellationToken: cancellationToken);

        if (!notifications.IsAvailable)
        {
            return new NotificationMirrorPollResult("degraded", 0, 0, 0, "core_unavailable", notifications.Message ?? "Den Core user-notification feed is unavailable.");
        }

        var mirrored = 0;
        var duplicates = 0;
        var skipped = 0;
        long maxSeenId = highWaterId ?? 0;
        string? nextCursor = after;

        foreach (var notification in notifications.Items)
        {
            var notificationId = ParseNotificationId(notification.Id);
            if (notificationId is not null && highWaterId is not null && notificationId <= highWaterId)
            {
                duplicates++;
                continue;
            }

            if (notificationId is not null && notificationId > maxSeenId)
            {
                maxSeenId = notificationId.Value;
                nextCursor = notification.Id;
            }
            else if (notificationId is null)
            {
                nextCursor = notification.Id;
            }

            // Extract the notification metadata type
            if (!notification.Metadata.TryGetValue("type", out var metadataType) || string.IsNullOrWhiteSpace(metadataType))
            {
                skipped++;
                continue;
            }

            // Filter by included metadata types
            if (!includedTypes.Contains(metadataType))
            {
                skipped++;
                continue;
            }

            var dedupeKey = $"core-user-notification:{notification.Id}:{metadataType}";

            // Build mirror metadata with non-waking defaults and canonical refs
            var mirrorMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["non_waking"] = "true",
                ["mirror_non_waking"] = "true",
                ["delivery_mode"] = "record_only",
                ["mirror_kind"] = "notification_mirror",
                ["sourceProjectId"] = notification.ProjectId ?? string.Empty,
                ["taskId"] = notification.TaskId ?? string.Empty,
                ["content"] = notification.Content ?? string.Empty,
                ["sender"] = notification.Sender ?? string.Empty,
                ["notificationId"] = notification.Id,
                ["metadataType"] = metadataType,
                ["urgency"] = notification.Urgency ?? "normal"
            };

            // Copy all original metadata keys prefixed with "core_"
            foreach (var kvp in notification.Metadata)
            {
                mirrorMetadata[$"core_{kvp.Key}"] = kvp.Value;
            }

            var mirrorMessage = new ChannelMirrorMessage(
                ChannelId: mirrorOptions.TargetChannelId,
                MessageKind: "notification_mirror",
                Body: FormatMirrorBody(notification),
                SourceKind: "user_notification",
                SourceId: notification.Id,
                DeepLink: null,
                DedupeKey: dedupeKey,
                Metadata: mirrorMetadata);

            var postResult = await _denChannelsClient.PostMirrorOrSystemMessageAsync(mirrorMessage, cancellationToken);
            if (!postResult.IsAvailable)
            {
                skipped++;
                continue;
            }

            mirrored++;
        }

        if (!string.IsNullOrWhiteSpace(nextCursor) && !string.Equals(nextCursor, after, StringComparison.Ordinal))
        {
            await _database.UpsertDeliveryLoopCursorAsync("notification_mirror", null, nextCursor, DateTimeOffset.UtcNow, cancellationToken);
        }

        return new NotificationMirrorPollResult("completed", mirrored, duplicates, skipped, null, null);
    }

    private static long? ParseNotificationId(string? notificationId)
    {
        return long.TryParse(notificationId, out var parsed) ? parsed : null;
    }

    private static string FormatMirrorBody(UserNotificationFeedItem notification)
    {
        var sender = notification.Sender ?? "unknown";
        var content = notification.Content ?? "(no content)";
        var project = notification.ProjectId is not null ? $"[{notification.ProjectId}] " : "";
        var task = notification.TaskId is not null ? $" (task #{notification.TaskId})" : "";
        return $"{project}{sender}{task}: {content}";
    }
}

public sealed record NotificationMirrorPollResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("mirrored_count")] int MirroredCount,
    [property: JsonPropertyName("duplicate_count")] int DuplicateCount,
    [property: JsonPropertyName("skipped_count")] int SkippedCount,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("message")] string? Message);
