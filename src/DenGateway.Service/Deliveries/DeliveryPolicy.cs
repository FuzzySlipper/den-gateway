namespace DenGateway.Service.Deliveries;

/// <summary>
/// Delivery policy evaluation with configurable brakes and channel-scoped overrides.
/// Static Evaluate(input) preserves backward compatibility with default safe behavior.
/// Evaluate(input, options) applies global and channel-override settings.
/// </summary>
public static class DeliveryPolicy
{
    private static readonly HashSet<string> SafeDeliveryModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "record_only",
        "notify",
        "wake",
        "pause",
        "resume"
    };

    private static readonly HashSet<string> GatewayPausedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "planned_pause_pending",
        "pausing",
        "paused_den_maintenance",
        "resume_pending"
    };

    private static readonly DeliveryPolicyOptions DefaultOptions = new();

    /// <summary>
    /// Evaluate using default (safe) options. Preserves backward compatibility.
    /// </summary>
    public static DeliveryDecision Evaluate(DeliverySimulationInput input)
    {
        return Evaluate(input, DefaultOptions);
    }

    /// <summary>
    /// Evaluate using explicit delivery policy options with channel-scoped overrides.
    /// </summary>
    public static DeliveryDecision Evaluate(DeliverySimulationInput input, DeliveryPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var profile = ResolveProfile(options, input.ChannelId, input.ChannelSlug);

        // --- Unsafe delivery mode ---
        if (!SafeDeliveryModes.Contains(input.DeliveryMode))
        {
            return Suppressed("unsafe_delivery_mode", profile);
        }

        // --- Control deliveries bypass all brakes ---
        var isControlDelivery = input.DeliveryMode is "pause" or "resume";
        if (isControlDelivery)
        {
            return Pending(profile);
        }

        // --- Suppress self-messages ---
        if (profile.SuppressSelfMessages
            && string.Equals(input.SenderIdentity, input.TargetIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("self_message", profile);
        }

        // --- Suppress pure reactions ---
        if (profile.SuppressReactions
            && string.Equals(input.SourceMessageKind, "reaction", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("pure_reaction", profile);
        }

        // --- Suppress mirror summaries ---
        if (profile.SuppressMirrorSummaries
            && string.Equals(input.SourceMessageKind, "mirror_summary", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("mirror_summary_suppressed", profile);
        }

        // --- Dedupe brake ---
        if (profile.Deduplicate && input.DedupeAlreadySeen)
        {
            return Suppressed("duplicate_dedupe_key", profile);
        }

        // --- Target cooldown ---
        if (profile.TargetCooldownSeconds > 0 && input.TargetInCooldown)
        {
            return Suppressed("target_cooldown", profile);
        }

        // --- Auto-reply window ---
        if (profile.AutoReplyWindowSeconds > 0 && input.AutoReplyWindowExceeded)
        {
            return Suppressed("auto_reply_window_exceeded", profile);
        }

        // --- Cascade depth ---
        if (profile.CascadeDepthEnabled && input.CascadeDepth > profile.MaxCascadeDepth)
        {
            return Suppressed("cascade_depth_exceeded", profile);
        }

        // --- Agent tennis without human reset ---
        if (profile.AgentTennisWithoutHumanResetEnabled && input.AgentTennisWithoutHumanReset)
        {
            return Suppressed("agent_tennis_requires_human_reset", profile);
        }

        // --- Gateway paused states ---
        if (GatewayPausedStates.Contains(input.GatewayState))
        {
            return Suppressed("den_paused", profile);
        }

        // --- Gateway down ---
        if (string.Equals(input.GatewayState, "down_detected", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("den_unavailable", profile);
        }

        // --- Ambiguous target ---
        if (input.AmbiguousTarget)
        {
            return Suppressed("ambiguous_target", profile);
        }

        // --- No active binding ---
        if (!input.HasActiveBinding)
        {
            return Suppressed("no_active_binding", profile);
        }

        // --- Source expired ---
        if (input.SourceExpired)
        {
            return Suppressed("expired_source", profile);
        }

        // --- Unsupported wake policies ---
        if (string.Equals(input.WakePolicy, "all_messages_except_self", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("unsupported_policy", profile);
        }

        if (string.Equals(input.DeliveryMode, "wake", StringComparison.OrdinalIgnoreCase)
            && string.Equals(input.WakePolicy, "mentions_only", StringComparison.OrdinalIgnoreCase)
            && !input.HasExplicitMention)
        {
            return Suppressed("unsupported_policy", profile);
        }

        return Pending(profile);
    }

    /// <summary>
    /// Resolve effective settings for this evaluation by merging global defaults
    /// with the first matching channel override (matched by ChannelId or ChannelSlug).
    /// </summary>
    public static DeliveryPolicyProfile ResolveProfile(
        DeliveryPolicyOptions options,
        string? channelId,
        string? channelSlug)
    {
        // Find first matching override
        DeliveryPolicyChannelOverride? matchedOverride = null;
        string? matchedKey = null;

        if (options.ChannelOverrides is { Count: > 0 } overrides)
        {
            foreach (var (key, ovr) in overrides)
            {
                if (ovr is null) continue;

                var idMatch = !string.IsNullOrWhiteSpace(ovr.ChannelId)
                    && string.Equals(ovr.ChannelId, channelId, StringComparison.OrdinalIgnoreCase);
                var slugMatch = !string.IsNullOrWhiteSpace(ovr.ChannelSlug)
                    && string.Equals(ovr.ChannelSlug, channelSlug, StringComparison.OrdinalIgnoreCase);

                if (idMatch || slugMatch)
                {
                    matchedOverride = ovr;
                    matchedKey = key;
                    break;
                }
            }
        }

        var label = matchedOverride?.Label;
        var sourceLabel = matchedOverride is not null ? "channel_override" : "global_default";

        return new DeliveryPolicyProfile(
            SourceLabel: sourceLabel,
            OverrideKey: matchedKey,
            AppliedLabel: label,
            TargetCooldownSeconds: matchedOverride?.TargetCooldownSeconds ?? options.TargetCooldownSeconds,
            AutoReplyWindowSeconds: matchedOverride?.AutoReplyWindowSeconds ?? options.AutoReplyWindowSeconds,
            CascadeDepthEnabled: matchedOverride?.CascadeDepthEnabled ?? options.CascadeDepthEnabled,
            MaxCascadeDepth: matchedOverride?.MaxCascadeDepth ?? options.MaxCascadeDepth,
            AgentTennisWithoutHumanResetEnabled: matchedOverride?.AgentTennisWithoutHumanResetEnabled ?? options.AgentTennisWithoutHumanResetEnabled,
            Deduplicate: matchedOverride?.Deduplicate ?? options.Deduplicate,
            SuppressSelfMessages: matchedOverride?.SuppressSelfMessages ?? options.SuppressSelfMessages,
            SuppressReactions: matchedOverride?.SuppressReactions ?? options.SuppressReactions,
            SuppressMirrorSummaries: matchedOverride?.SuppressMirrorSummaries ?? options.SuppressMirrorSummaries);
    }

    private static DeliveryDecision Pending(DeliveryPolicyProfile profile)
        => new("pending", null, true, profile.AppliedLabel, profile.OverrideKey);

    private static DeliveryDecision Suppressed(string reason, DeliveryPolicyProfile profile)
        => new("suppressed", reason, false, profile.AppliedLabel, profile.OverrideKey);
}

public sealed record DeliverySimulationInput(
    string SourceKind,
    string SourceMessageKind,
    string SenderType,
    string SenderIdentity,
    string TargetType,
    string TargetIdentity,
    string DeliveryMode,
    string WakePolicy,
    string GatewayState,
    bool HasExplicitMention,
    bool DedupeAlreadySeen,
    bool TargetInCooldown,
    bool AutoReplyWindowExceeded,
    int CascadeDepth,
    int MaxCascadeDepth,
    bool AgentTennisWithoutHumanReset,
    bool AmbiguousTarget,
    bool HasActiveBinding,
    bool SourceExpired,
    string? ChannelId = null,
    string? ChannelSlug = null);

/// <summary>
/// Result of a single DeliveryPolicy evaluation.
/// Includes metadata about which policy profile and override were applied.
/// </summary>
public sealed record DeliveryDecision(
    string Status,
    string? SuppressionReason,
    bool ShouldDeliver,
    string? AppliedPolicyLabel = null,
    string? AppliedOverrideKey = null);

public sealed class DeliveryLifecycle
{
    public DeliveryLifecycle(string status, int attemptCount = 0)
    {
        Status = status;
        AttemptCount = attemptCount;
    }

    public string Status { get; private set; }
    public int AttemptCount { get; private set; }

    public void MarkDelivering()
    {
        RequireStatus("pending");
        AttemptCount += 1;
        Status = "delivering";
    }

    public void MarkDelivered()
    {
        RequireStatus("delivering");
        Status = "delivered";
    }

    public void MarkAcknowledged()
    {
        RequireStatus("delivered");
        Status = "acknowledged";
    }

    public void MarkCompleted()
    {
        if (Status is not ("acknowledged" or "delivered"))
        {
            throw new InvalidOperationException($"Cannot complete delivery from status '{Status}'.");
        }

        Status = "completed";
    }

    public void MarkFailed()
    {
        if (Status is not ("pending" or "delivering"))
        {
            throw new InvalidOperationException($"Cannot fail delivery from status '{Status}'.");
        }

        Status = "failed";
    }

    public void MarkExpired()
    {
        if (Status is "completed" or "suppressed")
        {
            throw new InvalidOperationException($"Cannot expire delivery from status '{Status}'.");
        }

        Status = "expired";
    }

    private void RequireStatus(string expected)
    {
        if (!string.Equals(Status, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected delivery status '{expected}' but was '{Status}'.");
        }
    }
}
