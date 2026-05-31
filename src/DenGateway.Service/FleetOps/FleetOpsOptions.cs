using System.Text.Json.Serialization;

namespace DenGateway.Service.FleetOps;

/// <summary>
/// Options for the FleetOps feature, bound from configuration section "FleetOps".
/// </summary>
public sealed record FleetOpsOptions
{
    public const string SectionName = "FleetOps";

    /// <summary>Directory containing fleet maintenance scripts.</summary>
    public string ScriptsDirectory { get; init; } = "/home/agents/local/hermes-fleet/bin";

    /// <summary>Path to systemctl executable.</summary>
    public string SystemctlPath { get; init; } = "systemctl";

    /// <summary>Maximum number of output lines to retain per run.</summary>
    public int MaxOutputLines { get; init; } = 100;

    /// <summary>Maximum number of in-memory runs to retain.</summary>
    public int MaxRuns { get; init; } = 1000;

    /// <summary>Default timeout seconds for action execution.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 60;

    /// <summary>Timeout seconds for smoke/status actions.</summary>
    public int StatusTimeoutSeconds { get; init; } = 30;

    /// <summary>Timeout seconds for restart actions.</summary>
    public int RestartTimeoutSeconds { get; init; } = 120;
}
