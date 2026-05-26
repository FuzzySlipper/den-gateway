namespace DenGateway.Service.Deliveries;

/// <summary>
/// Typed appsettings options for DenGateway:DeliveryPolicy.
/// All brake thresholds default to current safe behavior when unset.
/// Channel-scoped overrides match by channel id and/or slug.
/// </summary>
public sealed class DeliveryPolicyOptions
{
    public const string SectionName = "DenGateway:DeliveryPolicy";

    // --- Global defaults (preserving current hard-coded behavior) ---

    /// <summary>Cooldown in seconds before same target can be woken again (default: 300 = 5 min).</summary>
    public int TargetCooldownSeconds { get; init; } = 300;

    /// <summary>Window in seconds during which auto-replies are permitted (default: 86400 = 24 h).</summary>
    public int AutoReplyWindowSeconds { get; init; } = 86400;

    /// <summary>Whether cascade depth checks are active (default: true).</summary>
    public bool CascadeDepthEnabled { get; init; } = true;

    /// <summary>Max cascade depth before suppression (default: 2).</summary>
    public int MaxCascadeDepth { get; init; } = 2;

    /// <summary>Suppress agent tennis chains without a human reset in the loop (default: true).</summary>
    public bool AgentTennisWithoutHumanResetEnabled { get; init; } = true;

    /// <summary>Keep duplicate dedupe suppression safe by default (default: true).</summary>
    public bool Deduplicate { get; init; } = true;

    /// <summary>Suppress self-messages (default: true).</summary>
    public bool SuppressSelfMessages { get; init; } = true;

    /// <summary>Suppress pure reactions (default: true).</summary>
    public bool SuppressReactions { get; init; } = true;

    /// <summary>Suppress mirror summaries (default: true).</summary>
    public bool SuppressMirrorSummaries { get; init; } = true;

    /// <summary>Channel-scoped overrides keyed by a unique name (e.g. "agent-tennis-test").</summary>
    public IReadOnlyDictionary<string, DeliveryPolicyChannelOverride> ChannelOverrides { get; init; } = new Dictionary<string, DeliveryPolicyChannelOverride>();
}

/// <summary>
/// Per-channel override for one or more brake settings.
/// Match by ChannelId (exact), ChannelSlug (exact), or both.
/// Null fields mean "use global default" for that setting.
/// </summary>
public sealed class DeliveryPolicyChannelOverride
{
    /// <summary>Exact channel id to match (e.g. "ch_abc123").</summary>
    public string? ChannelId { get; init; }

    /// <summary>Exact channel slug to match (e.g. "agent-tennis-test").</summary>
    public string? ChannelSlug { get; init; }

    // --- All overrideable brakes (null = inherit global default) ---

    public int? TargetCooldownSeconds { get; init; }
    public int? AutoReplyWindowSeconds { get; init; }
    public bool? CascadeDepthEnabled { get; init; }
    public int? MaxCascadeDepth { get; init; }
    public bool? AgentTennisWithoutHumanResetEnabled { get; init; }
    public bool? Deduplicate { get; init; }
    public bool? SuppressSelfMessages { get; init; }
    public bool? SuppressReactions { get; init; }
    public bool? SuppressMirrorSummaries { get; init; }

    /// <summary>
    /// Optional label identifying this override profile.
    /// Written into DeliveryDecision.AppliedPolicyLabel for observability.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>
/// Resolved effective settings for a single evaluation after merging
/// global defaults with a matching channel override, if any.
/// </summary>
public sealed record DeliveryPolicyProfile(
    string SourceLabel,
    string? OverrideKey,
    string? AppliedLabel,
    int TargetCooldownSeconds,
    int AutoReplyWindowSeconds,
    bool CascadeDepthEnabled,
    int MaxCascadeDepth,
    bool AgentTennisWithoutHumanResetEnabled,
    bool Deduplicate,
    bool SuppressSelfMessages,
    bool SuppressReactions,
    bool SuppressMirrorSummaries);
