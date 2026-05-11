using DenGateway.Service.Deliveries;

namespace DenGateway.Service.Tests;

public class DeliveryPolicyTests
{
    public static TheoryData<DeliverySimulationInput, string> SuppressionCases => new()
    {
        { BaseInput() with { SenderIdentity = "agent-a", TargetIdentity = "agent-a" }, "self_message" },
        { BaseInput() with { SourceMessageKind = "reaction" }, "pure_reaction" },
        { BaseInput() with { SourceMessageKind = "mirror_summary" }, "mirror_summary_suppressed" },
        { BaseInput() with { DedupeAlreadySeen = true }, "duplicate_dedupe_key" },
        { BaseInput() with { TargetInCooldown = true }, "target_cooldown" },
        { BaseInput() with { AutoReplyWindowExceeded = true }, "auto_reply_window_exceeded" },
        { BaseInput() with { CascadeDepth = 3, MaxCascadeDepth = 2 }, "cascade_depth_exceeded" },
        { BaseInput() with { AgentTennisWithoutHumanReset = true }, "agent_tennis_requires_human_reset" },
        { BaseInput() with { GatewayState = "paused_den_maintenance" }, "den_paused" },
        { BaseInput() with { GatewayState = "down_detected" }, "den_unavailable" },
        { BaseInput() with { AmbiguousTarget = true }, "ambiguous_target" },
        { BaseInput() with { HasActiveBinding = false }, "no_active_binding" },
        { BaseInput() with { SourceExpired = true }, "expired_source" },
        { BaseInput() with { DeliveryMode = "operator_override" }, "unsafe_delivery_mode" },
        { BaseInput() with { WakePolicy = "all_messages_except_self" }, "unsupported_policy" }
    };

    [Theory]
    [MemberData(nameof(SuppressionCases))]
    public void EvaluateSuppressesUnsafeWakeScenarios(DeliverySimulationInput input, string expectedReason)
    {
        var decision = DeliveryPolicy.Evaluate(input);

        Assert.Equal("suppressed", decision.Status);
        Assert.Equal(expectedReason, decision.SuppressionReason);
        Assert.False(decision.ShouldDeliver);
    }

    [Fact]
    public void EvaluateAllowsMentionWakeWithActiveBinding()
    {
        var decision = DeliveryPolicy.Evaluate(BaseInput() with { HasExplicitMention = true });

        Assert.Equal("pending", decision.Status);
        Assert.Null(decision.SuppressionReason);
        Assert.True(decision.ShouldDeliver);
    }

    [Fact]
    public void PauseAndResumeControlDeliveriesBypassPausedGatewaySuppression()
    {
        var pause = DeliveryPolicy.Evaluate(BaseInput() with { DeliveryMode = "pause", GatewayState = "down_detected" });
        var resume = DeliveryPolicy.Evaluate(BaseInput() with { DeliveryMode = "resume", GatewayState = "paused_den_maintenance" });

        Assert.True(pause.ShouldDeliver);
        Assert.Equal("pending", pause.Status);
        Assert.True(resume.ShouldDeliver);
        Assert.Equal("pending", resume.Status);
    }

    [Fact]
    public void DeliveryLifecycleTransitionsFollowAllowedOrder()
    {
        var lifecycle = new DeliveryLifecycle("pending");

        lifecycle.MarkDelivering();
        lifecycle.MarkDelivered();
        lifecycle.MarkAcknowledged();
        lifecycle.MarkCompleted();

        Assert.Equal("completed", lifecycle.Status);
        Assert.Equal(1, lifecycle.AttemptCount);
    }

    [Fact]
    public void DeliveryLifecycleRejectsInvalidTransitions()
    {
        var lifecycle = new DeliveryLifecycle("suppressed");

        Assert.Throws<InvalidOperationException>(() => lifecycle.MarkDelivering());
    }

    private static DeliverySimulationInput BaseInput() => new(
        SourceKind: "channel_message",
        SourceMessageKind: "human_text",
        SenderType: "user",
        SenderIdentity: "patch",
        TargetType: "agent",
        TargetIdentity: "agent-a",
        DeliveryMode: "wake",
        WakePolicy: "mentions_only",
        GatewayState: "normal",
        HasExplicitMention: false,
        DedupeAlreadySeen: false,
        TargetInCooldown: false,
        AutoReplyWindowExceeded: false,
        CascadeDepth: 0,
        MaxCascadeDepth: 2,
        AgentTennisWithoutHumanReset: false,
        AmbiguousTarget: false,
        HasActiveBinding: true,
        SourceExpired: false);
}
