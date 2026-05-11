namespace DenGateway.Service.Deliveries;

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

    public static DeliveryDecision Evaluate(DeliverySimulationInput input)
    {
        if (!SafeDeliveryModes.Contains(input.DeliveryMode))
        {
            return Suppressed("unsafe_delivery_mode");
        }

        var isControlDelivery = input.DeliveryMode is "pause" or "resume";
        if (isControlDelivery)
        {
            return Pending();
        }

        if (string.Equals(input.SenderIdentity, input.TargetIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("self_message");
        }

        if (string.Equals(input.SourceMessageKind, "reaction", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("pure_reaction");
        }

        if (string.Equals(input.SourceMessageKind, "mirror_summary", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("mirror_summary_suppressed");
        }

        if (input.DedupeAlreadySeen)
        {
            return Suppressed("duplicate_dedupe_key");
        }

        if (input.TargetInCooldown)
        {
            return Suppressed("target_cooldown");
        }

        if (input.AutoReplyWindowExceeded)
        {
            return Suppressed("auto_reply_window_exceeded");
        }

        if (input.CascadeDepth > input.MaxCascadeDepth)
        {
            return Suppressed("cascade_depth_exceeded");
        }

        if (input.AgentTennisWithoutHumanReset)
        {
            return Suppressed("agent_tennis_requires_human_reset");
        }

        if (GatewayPausedStates.Contains(input.GatewayState))
        {
            return Suppressed("den_paused");
        }

        if (string.Equals(input.GatewayState, "down_detected", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("den_unavailable");
        }

        if (input.AmbiguousTarget)
        {
            return Suppressed("ambiguous_target");
        }

        if (!input.HasActiveBinding)
        {
            return Suppressed("no_active_binding");
        }

        if (input.SourceExpired)
        {
            return Suppressed("expired_source");
        }

        if (string.Equals(input.WakePolicy, "all_messages_except_self", StringComparison.OrdinalIgnoreCase))
        {
            return Suppressed("unsupported_policy");
        }

        if (string.Equals(input.DeliveryMode, "wake", StringComparison.OrdinalIgnoreCase)
            && string.Equals(input.WakePolicy, "mentions_only", StringComparison.OrdinalIgnoreCase)
            && !input.HasExplicitMention)
        {
            return Suppressed("unsupported_policy");
        }

        return Pending();
    }

    private static DeliveryDecision Pending() => new("pending", null, true);
    private static DeliveryDecision Suppressed(string reason) => new("suppressed", reason, false);
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
    bool SourceExpired);

public sealed record DeliveryDecision(string Status, string? SuppressionReason, bool ShouldDeliver);

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
