namespace DenGateway.Service.DiscordBridge;

public sealed class DiscordBridgeOptions
{
    public const string SectionName = "DenGateway:DiscordBridge";

    public bool Enabled { get; init; }
    public string? BotToken { get; init; }
    public int CooldownSeconds { get; init; } = 30;
    public int MaxBodyLength { get; init; } = 2000;
    public IReadOnlyDictionary<string, DiscordBridgeTarget> Targets { get; init; } = new Dictionary<string, DiscordBridgeTarget>();
}

public sealed class DiscordBridgeTarget
{
    /// <summary>The Discord channel ID to post into.</summary>
    public string ChannelId { get; init; } = string.Empty;

    /// <summary>Optional thread ID within the channel.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Discord user ID to mention when WakeByMention is true.</summary>
    public string? MentionUserId { get; init; }

    /// <summary>If true, include a targeted mention of MentionUserId. If false, no mentions at all.</summary>
    public bool WakeByMention { get; init; } = true;
}
