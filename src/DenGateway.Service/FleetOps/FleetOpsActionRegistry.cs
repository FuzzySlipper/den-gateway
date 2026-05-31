using System.Collections.Concurrent;

namespace DenGateway.Service.FleetOps;

/// <summary>
/// Declarative registry of typed, allowlisted fleet operations actions.
/// Only entries in this registry can be executed; no free-form command/path/args.
/// </summary>
public sealed class FleetOpsActionRegistry
{
    private static readonly FleetActionArgSchema[] NoArgs = [];
    private static readonly IReadOnlyList<FleetActionArgSchema> ProfileArg = new[]
    {
        new FleetActionArgSchema("profile", "string", true,
            "Hermes profile name (e.g. spawned-coder, runner, den-mcp-runner)",
            "^[a-zA-Z0-9_-]+$")
    };

    private static readonly FleetOpsAction[] BuiltInActions =
    [
        // ---- Runnable actions ----

        new FleetOpsAction(
            Id: "fleet-status",
            Label: "Fleet Status",
            ScriptPath: "restart-agent-services",
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: false,
            RiskLevel: "low",
            SupportsDryRun: true,
            NeedsConfirmation: false,
            ConfirmationCopy: null,
            TimeoutSeconds: 60,
            MaxOutputLines: 100),

        new FleetOpsAction(
            Id: "fleet-smoke",
            Label: "Fleet Smoke Checks",
            ScriptPath: "smoke-hermes-fleet.sh",
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: false,
            RiskLevel: "low",
            SupportsDryRun: true,
            NeedsConfirmation: false,
            ConfirmationCopy: null,
            TimeoutSeconds: 60,
            MaxOutputLines: 100),

        new FleetOpsAction(
            Id: "restart-all",
            Label: "Restart All Gateway Services",
            ScriptPath: "restart-agent-services",
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: true,
            RiskLevel: "high",
            SupportsDryRun: true,
            NeedsConfirmation: true,
            ConfirmationCopy: "This will restart ALL Hermes gateway services. Active sessions may be interrupted.",
            TimeoutSeconds: 120,
            MaxOutputLines: 100,
            DryRunScriptPath: "restart-agent-services",
            DryRunSystemctlTemplate: null),

        new FleetOpsAction(
            Id: "restart-failed",
            Label: "Restart Failed Services Only",
            ScriptPath: "restart-agent-services",
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: true,
            RiskLevel: "medium",
            SupportsDryRun: true,
            NeedsConfirmation: false,
            ConfirmationCopy: null,
            TimeoutSeconds: 120,
            MaxOutputLines: 100,
            DryRunScriptPath: "restart-agent-services",
            DryRunSystemctlTemplate: null),

        new FleetOpsAction(
            Id: "restart-profile",
            Label: "Restart Profile Service",
            ScriptPath: null,
            SystemctlTemplate: "restart hermes-gateway@{profile}.service",
            ArgsSchema: ProfileArg,
            Mutating: true,
            RiskLevel: "medium",
            SupportsDryRun: true,
            NeedsConfirmation: false,
            ConfirmationCopy: null,
            TimeoutSeconds: 60,
            MaxOutputLines: 100,
            DryRunScriptPath: null,
            DryRunSystemctlTemplate: "is-active hermes-gateway@{profile}.service"),

        // ---- Visible-but-disabled actions ----

        new FleetOpsAction(
            Id: "fleet-update",
            Label: "Update Hermes Fleet",
            ScriptPath: null,
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: true,
            RiskLevel: "high",
            SupportsDryRun: true,
            NeedsConfirmation: true,
            ConfirmationCopy: null,
            TimeoutSeconds: 120,
            MaxOutputLines: 200,
            DisabledReason: "Requires explicit --restart-profiles; implement in follow-up task"),

        new FleetOpsAction(
            Id: "deploy-skills",
            Label: "Deploy Shared Skills",
            ScriptPath: null,
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: true,
            RiskLevel: "high",
            SupportsDryRun: true,
            NeedsConfirmation: true,
            ConfirmationCopy: null,
            TimeoutSeconds: 120,
            MaxOutputLines: 200,
            DisabledReason: "High-risk maintenance script; implement in follow-up task"),

        new FleetOpsAction(
            Id: "propagate-auth",
            Label: "Propagate Auth Credentials",
            ScriptPath: null,
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: true,
            RiskLevel: "high",
            SupportsDryRun: true,
            NeedsConfirmation: true,
            ConfirmationCopy: null,
            TimeoutSeconds: 120,
            MaxOutputLines: 200,
            DisabledReason: "Requires --apply flag and credential safety review; implement in follow-up task"),

        new FleetOpsAction(
            Id: "archive-launchers",
            Label: "Archive Stale Launchers",
            ScriptPath: null,
            SystemctlTemplate: null,
            ArgsSchema: NoArgs,
            Mutating: true,
            RiskLevel: "high",
            SupportsDryRun: true,
            NeedsConfirmation: true,
            ConfirmationCopy: null,
            TimeoutSeconds: 120,
            MaxOutputLines: 200,
            DisabledReason: "Privileged system-level operation; implement in follow-up task"),
    ];

    private readonly ConcurrentDictionary<string, FleetOpsAction> _actions;

    public FleetOpsActionRegistry()
    {
        _actions = new ConcurrentDictionary<string, FleetOpsAction>(
            BuiltInActions.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Get all registered actions.</summary>
    public IReadOnlyList<FleetOpsAction> GetAll() => BuiltInActions;

    /// <summary>Get a specific action by ID (case-insensitive).</summary>
    public FleetOpsAction? GetById(string id) =>
        _actions.TryGetValue(id, out var action) ? action : null;
}
