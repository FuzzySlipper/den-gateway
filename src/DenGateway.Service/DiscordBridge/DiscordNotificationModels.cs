using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenGateway.Service.DiscordBridge;

/// <summary>Request to post a notification to Discord via the Gateway-owned bridge.</summary>
public sealed record DiscordNotificationRequest(
    [property: JsonPropertyName("target_agent_identity")] string TargetAgentIdentity,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("source_channel_id")] string SourceChannelId,
    [property: JsonPropertyName("source_message_id")] string SourceMessageId,
    [property: JsonPropertyName("source_project_id")] string? SourceProjectId,
    [property: JsonPropertyName("requester")] string Requester,
    [property: JsonPropertyName("urgency")] string? Urgency,
    [property: JsonPropertyName("dedupe_key")] string DedupeKey,
    [property: JsonPropertyName("dry_run")] bool? DryRun);

/// <summary>Response from the notification endpoint.</summary>
public sealed record DiscordNotificationResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("notification_id")] long? NotificationId = null,
    [property: JsonPropertyName("attempt_id")] long? AttemptId = null,
    [property: JsonPropertyName("dry_run_payload")] DiscordDryRunPayload? DryRunPayload = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("deduped")] bool? Deduped = null);

/// <summary>Payload rendered for dry-run inspection.</summary>
public sealed record DiscordDryRunPayload(
    [property: JsonPropertyName("discord_channel_id")] string DiscordChannelId,
    [property: JsonPropertyName("discord_thread_id")] string? DiscordThreadId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("allowed_mentions")] DiscordDryRunAllowedMentions AllowedMentions);

/// <summary>Allowed mentions structure rendered for dry-run inspection.</summary>
public sealed record DiscordDryRunAllowedMentions(
    [property: JsonPropertyName("parse")] string[] Parse,
    [property: JsonPropertyName("users")] string[] Users,
    [property: JsonPropertyName("roles")] string[] Roles);

/// <summary>Persisted notification record.</summary>
public sealed record DiscordNotificationRecord(
    long Id,
    string DedupeKey,
    string TargetAgentIdentity,
    string Body,
    bool BodyTruncated,
    string SourceChannelId,
    string SourceMessageId,
    string? SourceProjectId,
    string Requester,
    string? Urgency,
    string? DiscordChannelId,
    string? DiscordThreadId,
    string? MentionUserId,
    bool WakeByMention,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Persisted attempt record.</summary>
public sealed record DiscordNotificationAttemptRecord(
    long Id,
    long NotificationId,
    int AttemptNumber,
    string Status,
    string? DiscordMessageId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

/// <summary>Result of attempting to send a notification.</summary>
public sealed record DiscordSendResult(
    string Status,
    string? DiscordMessageId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Discord API message payload.</summary>
public sealed record DiscordMessagePayload(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("allowed_mentions")] DiscordAllowedMentions? AllowedMentions = null);

/// <summary>Discord allowed_mentions object.</summary>
public sealed record DiscordAllowedMentions(
    [property: JsonPropertyName("parse")] string[] Parse,
    [property: JsonPropertyName("users")] string[]? Users = null,
    [property: JsonPropertyName("roles")] string[]? Roles = null);

/// <summary>Discord API message response.</summary>
public sealed record DiscordMessageResponse(
    [property: JsonPropertyName("id")] string Id);

/// <summary>Discord API error response.</summary>
public sealed record DiscordErrorResponse(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message);
