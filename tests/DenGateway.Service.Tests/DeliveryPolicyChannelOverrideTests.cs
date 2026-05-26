using DenGateway.Service.Deliveries;

namespace DenGateway.Service.Tests;

public class DeliveryPolicyChannelOverrideTests
{
    private static readonly DeliveryPolicyOptions DefaultOptions = new();
    private static readonly DeliveryPolicyOptions AgentTennisOverrideOptions = new()
    {
        TargetCooldownSeconds = 300,
        AutoReplyWindowSeconds = 86400,
        CascadeDepthEnabled = true,
        MaxCascadeDepth = 2,
        AgentTennisWithoutHumanResetEnabled = true,
        Deduplicate = true,
        SuppressSelfMessages = true,
        SuppressReactions = true,
        SuppressMirrorSummaries = true,
        ChannelOverrides = new Dictionary<string, DeliveryPolicyChannelOverride>
        {
            ["agent-tennis-test"] = new()
            {
                ChannelId = "ch_agent_tennis_test",
                ChannelSlug = "agent-tennis-test",
                TargetCooldownSeconds = 0,
                AutoReplyWindowSeconds = 864000,
                CascadeDepthEnabled = false,
                MaxCascadeDepth = 10,
                AgentTennisWithoutHumanResetEnabled = false,
                Deduplicate = true,
                SuppressSelfMessages = false,
                SuppressReactions = true,
                SuppressMirrorSummaries = true,
                Label = "agent-tennis-test-no-safeguards"
            }
        }
    };

    // --- Helpers for common agent-tennis chain inputs ---

    /// <summary>
    /// Simulates an A->B->A cascade where agent-b is replying to agent-a's message.
    /// All brakes are triggered: cascade depth exceeded, agent tennis flag set,
    /// target in cooldown, self-message would apply if identities matched.
    /// </summary>
    private static DeliverySimulationInput AgentTennisChainInput(string? channelId = null, string? channelSlug = null) => new(
        SourceKind: "channel_message",
        SourceMessageKind: "agent_text",
        SenderType: "agent",
        SenderIdentity: "agent-b",
        TargetType: "agent",
        TargetIdentity: "agent-a",
        DeliveryMode: "wake",
        WakePolicy: "mentions_only",
        GatewayState: "normal",
        HasExplicitMention: true,
        DedupeAlreadySeen: false,
        TargetInCooldown: true,      // A was just targeted
        AutoReplyWindowExceeded: false,
        CascadeDepth: 3,             // A->B->A->B is depth 3
        MaxCascadeDepth: 2,          // old default in input (ignored by new code)
        AgentTennisWithoutHumanReset: true,  // no human in loop
        AmbiguousTarget: false,
        HasActiveBinding: true,
        SourceExpired: false,
        ChannelId: channelId,
        ChannelSlug: channelSlug);

    /// <summary>
    /// Ordinary default channel input (no overriding channel id/slug).
    /// Same chain should be suppressed.
    /// </summary>
    private static DeliverySimulationInput DefaultChainInput() => AgentTennisChainInput(null, null);

    /// <summary>
    /// Override test channel input matching the agent-tennis-test override by slug.
    /// Should pass through all brakes.
    /// </summary>
    private static DeliverySimulationInput OverrideBySlugInput() => AgentTennisChainInput(
        channelId: null,
        channelSlug: "agent-tennis-test");

    /// <summary>
    /// Override test channel input matching the agent-tennis-test override by id.
    /// Should pass through all brakes.
    /// </summary>
    private static DeliverySimulationInput OverrideByIdInput() => AgentTennisChainInput(
        channelId: "ch_agent_tennis_test",
        channelSlug: null);

    // ===================================================================
    // Default behavior: all brakes active
    // ===================================================================

    [Fact]
    public void DefaultOptions_SuppressesAgentTennisChain_ByCascadeDepth()
    {
        // A->B->A->B cascade at depth 3 exceeds default MaxCascadeDepth=2.
        // Cooldown is false so we specifically test the cascade depth brake.
        var input = DefaultChainInput() with { TargetInCooldown = false };

        var decision = DeliveryPolicy.Evaluate(input, DefaultOptions);

        Assert.Equal("suppressed", decision.Status);
        Assert.Equal("cascade_depth_exceeded", decision.SuppressionReason);
        Assert.False(decision.ShouldDeliver);
        Assert.Null(decision.AppliedPolicyLabel);   // no override
        Assert.Null(decision.AppliedOverrideKey);
    }

    [Fact]
    public void DefaultOptions_SuppressesAgentTennisChain_ByAgentTennisFlag()
    {
        // Test what happens if cascade depth is within bounds but agent tennis is flagged.
        var input = DefaultChainInput() with
        {
            CascadeDepth = 1,          // within max depth
            TargetInCooldown = false,  // not in cooldown
        };

        var decision = DeliveryPolicy.Evaluate(input, DefaultOptions);

        Assert.Equal("suppressed", decision.Status);
        Assert.Equal("agent_tennis_requires_human_reset", decision.SuppressionReason);
        Assert.False(decision.ShouldDeliver);
    }

    [Fact]
    public void DefaultOptions_SuppressesAgentTennisChain_ByCooldown()
    {
        // Test what happens if only the cooldown brake is triggered.
        var input = DefaultChainInput() with
        {
            CascadeDepth = 0,
            AgentTennisWithoutHumanReset = false,
            TargetInCooldown = true,
        };

        var decision = DeliveryPolicy.Evaluate(input, DefaultOptions);

        Assert.Equal("suppressed", decision.Status);
        Assert.Equal("target_cooldown", decision.SuppressionReason);
        Assert.False(decision.ShouldDeliver);
    }

    // ===================================================================
    // Channel override: all brakes relaxed for test channel
    // ===================================================================

    [Fact]
    public void ChannelOverride_BySlug_AllowsAgentTennisChain_PastAllBrakes()
    {
        // Override by slug: "agent-tennis-test"
        var input = OverrideBySlugInput();
        var decision = DeliveryPolicy.Evaluate(input, AgentTennisOverrideOptions);

        Assert.Equal("pending", decision.Status);                    // not suppressed
        Assert.Null(decision.SuppressionReason);
        Assert.True(decision.ShouldDeliver);
        Assert.Equal("agent-tennis-test-no-safeguards", decision.AppliedPolicyLabel);
        Assert.Equal("agent-tennis-test", decision.AppliedOverrideKey);
    }

    [Fact]
    public void ChannelOverride_ById_AllowsAgentTennisChain_PastAllBrakes()
    {
        // Override by channel id: "ch_agent_tennis_test"
        var input = OverrideByIdInput();
        var decision = DeliveryPolicy.Evaluate(input, AgentTennisOverrideOptions);

        Assert.Equal("pending", decision.Status);
        Assert.Null(decision.SuppressionReason);
        Assert.True(decision.ShouldDeliver);
        Assert.Equal("agent-tennis-test-no-safeguards", decision.AppliedPolicyLabel);
        Assert.Equal("agent-tennis-test", decision.AppliedOverrideKey);
    }

    [Fact]
    public void ChannelOverride_AllowsDeepCascade_BeyondDefaultMax()
    {
        // Cascade depth 3 but override sets CascadeDepthEnabled=false
        var decision = DeliveryPolicy.Evaluate(
            OverrideBySlugInput(),
            AgentTennisOverrideOptions);

        Assert.Equal("pending", decision.Status);
        Assert.True(decision.ShouldDeliver);
    }

    [Fact]
    public void ChannelOverride_AllowsSelfMessages()
    {
        // Override has SuppressSelfMessages=false
        var input = OverrideBySlugInput() with
        {
            SenderIdentity = "agent-a",
            TargetIdentity = "agent-a",
        };

        var decision = DeliveryPolicy.Evaluate(input, AgentTennisOverrideOptions);

        Assert.Equal("pending", decision.Status);  // self-message brake disabled
        Assert.True(decision.ShouldDeliver);
    }

    [Fact]
    public void ChannelOverride_IgnoresCooldown()
    {
        // Override has TargetCooldownSeconds=0 meaning cooldown is effectively disabled
        // Note: the bool TargetInCooldown flag is still true in the input.
        // The override only controls the threshold; the actual cooldown check
        // is done upstream. The upstream needs to use TargetCooldownSeconds to
        // compute the flag. For this test's purpose, the flag being true but
        // the override saying cooldown is disabled means the upstream should
        // never set the flag to true.
        // 
        // Practically, TargetCooldownSeconds=0 means the upstream evaluator
        // would compute TargetInCooldown=false for any cooldown > 0 elapsed.
        // The bool flag is the pre-computed result; the config controls the
        // computation. Here we verify that the policy evaluations reflect the
        // profile's cooldown setting.
        var profile = DeliveryPolicy.ResolveProfile(AgentTennisOverrideOptions, "ch_agent_tennis_test", null);
        Assert.Equal(0, profile.TargetCooldownSeconds);  // effectively disabled
    }

    // ===================================================================
    // Ordinary/default channel still suppresses the same chain
    // ===================================================================

    [Fact]
    public void DefaultChannelWithoutOverride_StillSuppresses_AgentTennisChain()
    {
        // Input with a channel ID that does NOT match any override
        var input = AgentTennisChainInput(
            channelId: "ch_some_other_channel",
            channelSlug: "production-team");

        var decision = DeliveryPolicy.Evaluate(input, AgentTennisOverrideOptions);

        Assert.Equal("suppressed", decision.Status);
        Assert.False(decision.ShouldDeliver);
        Assert.Null(decision.AppliedPolicyLabel);   // no override applied
        Assert.Null(decision.AppliedOverrideKey);
    }

    [Fact]
    public void NullChannel_WithoutOverride_StillSuppressed()
    {
        // No channel id/slug at all => no override match
        var input = DefaultChainInput();
        var decision = DeliveryPolicy.Evaluate(input, AgentTennisOverrideOptions);

        Assert.Equal("suppressed", decision.Status);
        Assert.False(decision.ShouldDeliver);
        Assert.Null(decision.AppliedPolicyLabel);
        Assert.Null(decision.AppliedOverrideKey);
    }

    // ===================================================================
    // ResolveProfile tests
    // ===================================================================

    [Fact]
    public void ResolveProfile_NoMatch_ReturnsGlobalDefaults()
    {
        var profile = DeliveryPolicy.ResolveProfile(DefaultOptions, null, null);

        Assert.Equal("global_default", profile.SourceLabel);
        Assert.Null(profile.OverrideKey);
        Assert.Null(profile.AppliedLabel);
        Assert.Equal(300, profile.TargetCooldownSeconds);
        Assert.Equal(86400, profile.AutoReplyWindowSeconds);
        Assert.True(profile.CascadeDepthEnabled);
        Assert.Equal(2, profile.MaxCascadeDepth);
        Assert.True(profile.AgentTennisWithoutHumanResetEnabled);
        Assert.True(profile.Deduplicate);
        Assert.True(profile.SuppressSelfMessages);
        Assert.True(profile.SuppressReactions);
        Assert.True(profile.SuppressMirrorSummaries);
    }

    [Fact]
    public void ResolveProfile_MatchById_ReturnsOverrideValues()
    {
        var profile = DeliveryPolicy.ResolveProfile(
            AgentTennisOverrideOptions,
            "ch_agent_tennis_test",
            null);

        Assert.Equal("channel_override", profile.SourceLabel);
        Assert.Equal("agent-tennis-test", profile.OverrideKey);
        Assert.Equal("agent-tennis-test-no-safeguards", profile.AppliedLabel);
        Assert.Equal(0, profile.TargetCooldownSeconds);
        Assert.Equal(864000, profile.AutoReplyWindowSeconds);
        Assert.False(profile.CascadeDepthEnabled);
        Assert.Equal(10, profile.MaxCascadeDepth);
        Assert.False(profile.AgentTennisWithoutHumanResetEnabled);
    }

    [Fact]
    public void ResolveProfile_MatchBySlug_ReturnsOverrideValues()
    {
        var profile = DeliveryPolicy.ResolveProfile(
            AgentTennisOverrideOptions,
            null,
            "agent-tennis-test");

        Assert.Equal("channel_override", profile.SourceLabel);
        Assert.Equal("agent-tennis-test", profile.OverrideKey);
        Assert.Equal(0, profile.TargetCooldownSeconds);
        Assert.False(profile.CascadeDepthEnabled);
        Assert.False(profile.AgentTennisWithoutHumanResetEnabled);
    }

    [Fact]
    public void ResolveProfile_FirstMatchWins_WhenMultipleOverrides()
    {
        var multiOptions = new DeliveryPolicyOptions
        {
            ChannelOverrides = new Dictionary<string, DeliveryPolicyChannelOverride>
            {
                ["first"] = new()
                {
                    ChannelId = "ch_target",
                    Label = "first-override",
                    CascadeDepthEnabled = false
                },
                ["second"] = new()
                {
                    ChannelId = "ch_target",
                    Label = "second-override",
                    CascadeDepthEnabled = true,
                    MaxCascadeDepth = 5
                }
            }
        };

        var profile = DeliveryPolicy.ResolveProfile(multiOptions, "ch_target", null);

        Assert.Equal("first-override", profile.AppliedLabel);
        Assert.Equal("first", profile.OverrideKey);
        Assert.False(profile.CascadeDepthEnabled);  // first wins
    }

    // ===================================================================
    // Static default Evaluate preserves backward compat
    // ===================================================================

    [Fact]
    public void StaticEvaluate_WithoutOptions_UsesDefaults()
    {
        // Identical to the original test case from DeliveryPolicyTests
        var input = new DeliverySimulationInput(
            SourceKind: "channel_message",
            SourceMessageKind: "human_text",
            SenderType: "user",
            SenderIdentity: "patch",
            TargetType: "agent",
            TargetIdentity: "agent-a",
            DeliveryMode: "wake",
            WakePolicy: "mentions_only",
            GatewayState: "normal",
            HasExplicitMention: true,
            DedupeAlreadySeen: false,
            TargetInCooldown: false,
            AutoReplyWindowExceeded: false,
            CascadeDepth: 0,
            MaxCascadeDepth: 2,
            AgentTennisWithoutHumanReset: false,
            AmbiguousTarget: false,
            HasActiveBinding: true,
            SourceExpired: false);

        var decision = DeliveryPolicy.Evaluate(input);  // no options = default

        Assert.Equal("pending", decision.Status);
        Assert.Null(decision.SuppressionReason);
        Assert.True(decision.ShouldDeliver);
    }
}
