using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DenGateway.Service.DiscordBridge;

/// <summary>
/// Gateway-owned outbound Discord bridge service for Channel-originated notification/wake requests.
/// This is infrastructure, not an LLM ambassador workflow.
/// </summary>
public sealed class DiscordNotificationService
{
    private readonly DiscordNotificationRepository _repository;
    private readonly DiscordApiClient _apiClient;
    private readonly IOptions<DiscordBridgeOptions> _options;

    public DiscordNotificationService(
        DiscordNotificationRepository repository,
        DiscordApiClient apiClient,
        IOptions<DiscordBridgeOptions> options)
    {
        _repository = repository;
        _apiClient = apiClient;
        _options = options;
    }

    /// <summary>
    /// Process a notification request: validate target, check dedupe/cooldown,
    /// truncate body, send to Discord (or dry-run), record attempt.
    /// </summary>
    public async Task<DiscordNotificationResponse> NotifyAsync(
        DiscordNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var now = DateTimeOffset.UtcNow;
        var dryRun = request.DryRun ?? false;

        // 1. Validate target exists in config
        if (!options.Targets.TryGetValue(request.TargetAgentIdentity, out var target))
        {
            return new DiscordNotificationResponse(
                Status: "rejected",
                Error: $"Unknown target agent identity: {request.TargetAgentIdentity}. No Discord notification sent.");
        }

        // 2. Truncate body if needed
        var (body, bodyTruncated) = TruncateBody(request.Body, options.MaxBodyLength);

        // 3. Build Discord payload
        var content = BuildDiscordContent(body, request, target);
        var allowedMentions = BuildAllowedMentions(target);

        // 4. Dry run: return the rendered payload without sending
        if (dryRun)
        {
            return new DiscordNotificationResponse(
                Status: "dry_run",
                DryRunPayload: new DiscordDryRunPayload(
                    DiscordChannelId: target.ChannelId,
                    DiscordThreadId: target.ThreadId,
                    Content: content,
                    AllowedMentions: new DiscordDryRunAllowedMentions(
                        Parse: allowedMentions?.Parse ?? [],
                        Users: allowedMentions?.Users ?? [],
                        Roles: allowedMentions?.Roles ?? [])));
        }

        // 5. Check dedupe - try to insert; if dedupe key exists, it's already handled
        var notificationId = await _repository.TryInsertNotificationAsync(
            request.DedupeKey,
            request.TargetAgentIdentity,
            body,
            bodyTruncated,
            request.SourceChannelId,
            request.SourceMessageId,
            request.SourceProjectId,
            request.Requester,
            request.Urgency,
            target.ChannelId,
            target.ThreadId,
            target.MentionUserId,
            target.WakeByMention,
            now,
            cancellationToken);

        if (notificationId is null)
        {
            // Dedupe key already exists - this is a duplicate request
            var existingId = await _repository.FindNotificationByDedupeKeyAsync(request.DedupeKey, cancellationToken);
            return new DiscordNotificationResponse(
                Status: "deduped",
                NotificationId: existingId,
                Deduped: true);
        }

        // 6. Check per-target cooldown (exclude the just-inserted record)
        var inCooldown = await _repository.IsInCooldownAsync(
            request.TargetAgentIdentity,
            options.CooldownSeconds,
            now,
            excludeNotificationId: notificationId.Value,
            cancellationToken);

        if (inCooldown)
        {
            await _repository.RecordAttemptAsync(
                notificationId.Value,
                attemptNumber: 1,
                status: "cooldown",
                discordMessageId: null,
                errorCode: "COOLDOWN",
                errorMessage: $"Target {request.TargetAgentIdentity} is in cooldown ({options.CooldownSeconds}s).",
                payloadJson: null,
                now,
                cancellationToken);

            return new DiscordNotificationResponse(
                Status: "cooldown",
                NotificationId: notificationId.Value,
                Error: $"Target {request.TargetAgentIdentity} is in cooldown. Skipping Discord send.");
        }

        // 7. Send to Discord
        var payload = new DiscordMessagePayload(Content: content, AllowedMentions: allowedMentions);

        var serializedPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var sendResult = await _apiClient.SendMessageAsync(target.ChannelId, target.ThreadId, payload, cancellationToken);

        // 8. Record attempt
        var attemptId = await _repository.RecordAttemptAsync(
            notificationId.Value,
            attemptNumber: 1,
            sendResult.Status,
            sendResult.DiscordMessageId,
            sendResult.ErrorCode,
            sendResult.ErrorMessage,
            serializedPayload,
            now,
            cancellationToken);

        // 9. Return response
        if (sendResult.Status == "sent")
        {
            return new DiscordNotificationResponse(
                Status: "sent",
                NotificationId: notificationId.Value,
                AttemptId: attemptId);
        }

        return new DiscordNotificationResponse(
            Status: sendResult.Status,
            NotificationId: notificationId.Value,
            AttemptId: attemptId,
            Error: sendResult.ErrorMessage);
    }

    /// <summary>Truncate body to max length, returning truncated flag.</summary>
    public static (string Body, bool Truncated) TruncateBody(string body, int maxLength)
    {
        if (string.IsNullOrEmpty(body))
            return (body, false);

        if (body.Length <= maxLength)
            return (body, false);

        return (body[..(maxLength - 3)] + "...", true);
    }

    /// <summary>Build the content string with source attribution and optional mention prefix.</summary>
    private static string BuildDiscordContent(string body, DiscordNotificationRequest request, DiscordBridgeTarget target)
    {
        var header = $"🔔 **Notification from {request.Requester}**";
        if (!string.IsNullOrWhiteSpace(request.SourceProjectId))
        {
            header = $"🔔 **{request.Requester}** (project: {request.SourceProjectId})";
        }

        var urgencyLine = !string.IsNullOrWhiteSpace(request.Urgency)
            ? $"\n**Urgency**: {request.Urgency}"
            : "";

        var sourceLine = request.SourceChannelId is not null
            ? $"\n*Source: channel {request.SourceChannelId}, message {request.SourceMessageId}*"
            : $"\n*Source: message {request.SourceMessageId}*";

        var content = $"{header}{urgencyLine}\n\n{body}{sourceLine}";

        // Prepend a Discord-native mention token when WakeByMention is true and a user is configured.
        // This creates the actual ping; allowed_mentions only permits it, it does not generate the token.
        if (target.WakeByMention && !string.IsNullOrWhiteSpace(target.MentionUserId))
        {
            content = $"<@{target.MentionUserId}> {content}";
        }

        return content;
    }

    /// <summary>Build allowed_mentions with deliberate scope: only configured target user when WakeByMention=true.</summary>
    private static DiscordAllowedMentions? BuildAllowedMentions(DiscordBridgeTarget target)
    {
        if (target.WakeByMention && !string.IsNullOrWhiteSpace(target.MentionUserId))
        {
            // Deliberate mention of the configured target user, suppress all broad mentions
            return new DiscordAllowedMentions(
                Parse: [],  // suppress @everyone, @here, role pings
                Users: [target.MentionUserId]);
        }

        // No mentions at all when WakeByMention is false
        return new DiscordAllowedMentions(
            Parse: [],
            Users: []);
    }

    /// <summary>Validate the request has all required fields.</summary>
    public static bool ValidateRequest(DiscordNotificationRequest request, out string? error)
    {
        if (string.IsNullOrWhiteSpace(request.TargetAgentIdentity))
        {
            error = "target_agent_identity is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            error = "body is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.SourceChannelId))
        {
            error = "source_channel_id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.SourceMessageId))
        {
            error = "source_message_id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Requester))
        {
            error = "requester is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.DedupeKey))
        {
            error = "dedupe_key is required.";
            return false;
        }

        error = null;
        return true;
    }
}
