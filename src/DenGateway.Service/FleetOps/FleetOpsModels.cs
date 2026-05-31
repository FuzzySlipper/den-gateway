using System.Text.Json.Serialization;

namespace DenGateway.Service.FleetOps;

/// <summary>Read model for GET /api/gateway/fleet-ops.</summary>
public sealed class FleetOpsOverviewResponse
{
    [JsonPropertyName("service")]
    public string Service { get; init; } = "den-gateway";

    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("serviceUnits")]
    public required IReadOnlyList<FleetServiceUnit> ServiceUnits { get; init; }

    [JsonPropertyName("actions")]
    public required IReadOnlyList<FleetActionDescriptor> Actions { get; init; }

    [JsonPropertyName("discoveryDiagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiscoveryDiagnostics { get; init; }

    [JsonPropertyName("recentRuns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FleetOpsActionRun>? RecentRuns { get; init; }
}

/// <summary>A discovered systemd service unit.</summary>
public sealed record FleetServiceUnit(
    [property: JsonPropertyName("unitName")] string UnitName,
    [property: JsonPropertyName("profileName")] string ProfileName,
    [property: JsonPropertyName("activeState")] string ActiveState,
    [property: JsonPropertyName("subState")] string SubState,
    [property: JsonPropertyName("pid")] int? Pid = null,
    [property: JsonPropertyName("statusSummary")] string? StatusSummary = null)
{
    /// <summary>Human-readable status description.</summary>
    [JsonPropertyName("description")]
    public string Description => $"{ProfileName} ({ActiveState}/{SubState})";
}

/// <summary>An action descriptor shown in the fleet-ops overview.</summary>
public sealed record FleetActionDescriptor(
    [property: JsonPropertyName("actionId")] string ActionId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("riskLevel")] string RiskLevel,
    [property: JsonPropertyName("mutating")] bool Mutating,
    [property: JsonPropertyName("supportsDryRun")] bool SupportsDryRun,
    [property: JsonPropertyName("needsConfirmation")] bool NeedsConfirmation,
    [property: JsonPropertyName("confirmationCopy")] string? ConfirmationCopy = null,
    [property: JsonPropertyName("timeoutSeconds")] int TimeoutSeconds = 60,
    [property: JsonPropertyName("argsSchema")] IReadOnlyList<FleetActionArgSchema>? ArgsSchema = null,
    [property: JsonPropertyName("disabledReason")] string? DisabledReason = null);

/// <summary>Argument schema for an action.</summary>
public sealed record FleetActionArgSchema(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("pattern")] string? Pattern = null);

/// <summary>Request to run a fleet action.</summary>
public sealed record FleetOpsActionRunRequest(
    [property: JsonPropertyName("actionId")] string ActionId,
    [property: JsonPropertyName("dryRun")] bool DryRun = false,
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, string>? Args = null,
    [property: JsonPropertyName("confirmation")] string? Confirmation = null);

/// <summary>Result/state of a fleet action run.</summary>
public sealed record FleetOpsActionRun(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("actionId")] string ActionId,
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, string> Args,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt = null,
    [property: JsonPropertyName("exitCode")] int? ExitCode = null,
    [property: JsonPropertyName("stdoutTail")] IReadOnlyList<string>? StdoutTail = null,
    [property: JsonPropertyName("stderrTail")] IReadOnlyList<string>? StderrTail = null,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null,
    [property: JsonPropertyName("wasDryRun")] bool WasDryRun = false);

/// <summary>Internal action definition for the registry.</summary>
public sealed record FleetOpsAction(
    string Id,
    string Label,
    string? ScriptPath,
    string? SystemctlTemplate,
    IReadOnlyList<FleetActionArgSchema> ArgsSchema,
    bool Mutating,
    string RiskLevel,
    bool SupportsDryRun,
    bool NeedsConfirmation,
    string? ConfirmationCopy,
    int TimeoutSeconds,
    int MaxOutputLines = 100,
    string? DisabledReason = null,
    string? DryRunScriptPath = null,
    string? DryRunSystemctlTemplate = null);

/// <summary>Result from command execution.</summary>
public sealed record CommandResult(
    int ExitCode,
    IReadOnlyList<string> StdoutLines,
    IReadOnlyList<string> StderrLines,
    string? ErrorMessage = null);

/// <summary>Response for run lookup.</summary>
public sealed record FleetOpsRunResponse(
    [property: JsonPropertyName("run")] FleetOpsActionRun? Run);
