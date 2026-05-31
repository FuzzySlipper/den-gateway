using System.Globalization;
using System.Text.RegularExpressions;

namespace DenGateway.Service.FleetOps;

/// <summary>
/// Orchestrates fleet operations: service unit discovery, action resolution,
/// command execution, run tracking, and audit logging.
/// Only acts on typed, allowlisted actions from FleetOpsActionRegistry.
/// No free-form command, path, or args are accepted.
/// </summary>
public sealed partial class FleetOpsService
{
    private readonly FleetOpsActionRegistry _registry;
    private readonly IFleetOpsServiceUnitDiscovery _discovery;
    private readonly IFleetOpsCommandExecutor _executor;
    private readonly IFleetOpsRunStore _runStore;
    private readonly FleetOpsOptions _options;
    private readonly ILogger<FleetOpsService> _logger;

    public FleetOpsService(
        FleetOpsActionRegistry registry,
        IFleetOpsServiceUnitDiscovery discovery,
        IFleetOpsCommandExecutor executor,
        IFleetOpsRunStore runStore,
        FleetOpsOptions options,
        ILogger<FleetOpsService> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get the fleet-ops overview: discovered service units + available actions + recent runs.
    /// </summary>
    public async Task<FleetOpsOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Discover service units
        var (units, diagnostics) = await _discovery.DiscoverAsync(cancellationToken);

        // Build action descriptors from the registry
        var actions = _registry.GetAll().Select(BuildDescriptor).ToList();

        // Get recent runs
        var recentRuns = _runStore.GetRecentRuns(10);

        return new FleetOpsOverviewResponse
        {
            GeneratedAt = now,
            ServiceUnits = units,
            Actions = actions,
            DiscoveryDiagnostics = diagnostics,
            RecentRuns = recentRuns.Count > 0 ? recentRuns : null
        };
    }

    /// <summary>
    /// Execute a typed, allowlisted fleet action.
    /// Validates actionId, args, confirmation, discovery constraints, and dry-run semantics.
    /// </summary>
    public async Task<FleetOpsActionRun> ExecuteActionAsync(string actionId, FleetOpsActionRunRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");

        // 1. Resolve action from registry
        var action = _registry.GetById(actionId);
        if (action is null)
        {
            return CreateErrorRun(runId, actionId, request, now, $"Unknown action: {actionId}");
        }

        // 2. Check if disabled
        if (!string.IsNullOrEmpty(action.DisabledReason))
        {
            return CreateErrorRun(runId, actionId, request, now, $"Action '{actionId}' is disabled: {action.DisabledReason}");
        }

        // 3. Validate args against schema
        var args = request.Args ?? new Dictionary<string, string>();
        var validationError = ValidateArgs(action, args);
        if (validationError is not null)
        {
            return CreateErrorRun(runId, actionId, request, now, validationError);
        }

        // 4. Check confirmation for mutating/high-risk actions
        if (action.NeedsConfirmation)
        {
            var confirmation = request.Confirmation ?? string.Empty;
            if (!IsConfirmationValid(confirmation, action))
            {
                _logger.LogWarning("Rejected {ActionId}: missing or invalid confirmation", actionId);
                return CreateErrorRun(runId, actionId, request, now, "Confirmation is required for this action. Provide a 'confirmation' field.");
            }
        }

        // 5. For restart-profile, validate profile is a discovered unit
        if (action.Id == "restart-profile" && args.TryGetValue("profile", out var profile))
        {
            var (discoveredUnits, discoverDiag) = await _discovery.DiscoverAsync(cancellationToken);
            var isKnown = discoveredUnits.Any(u =>
                string.Equals(u.ProfileName, profile, StringComparison.OrdinalIgnoreCase));
            if (!isKnown)
            {
                var reason = discoverDiag ?? $"Profile '{profile}' not found in discovered service units";
                _logger.LogWarning("Rejecting restart-profile: {Reason}", reason);
                return CreateErrorRun(runId, actionId, request, now, reason);
            }
        }

        // 6. Build the command (resolved executable + fixed argv)
        var isDryRun = request.DryRun && action.SupportsDryRun;
        var builtCommand = BuildCommand(action, args, isDryRun);

        if (builtCommand is null)
        {
            return CreateErrorRun(runId, actionId, request, now, $"Action '{actionId}' has no executable or systemctl template configured");
        }

        var (executable, argv) = builtCommand.Value;

        // 7. Create and store the run record
        var run = new FleetOpsActionRun(
            RunId: runId,
            ActionId: actionId,
            Args: args,
            Status: "queued",
            CreatedAt: now,
            WasDryRun: isDryRun);

        _runStore.AddRun(run);
        _runStore.UpdateRunStarted(runId);

        // 8. Execute (skip actual execution for dry-run of non-mutating actions that just preview)
        if (isDryRun && action.Mutating && action.DryRunScriptPath is null && action.DryRunSystemctlTemplate is null)
        {
            // No preview command defined for dry-run of this mutating action
            _runStore.UpdateRun(runId, 0, "completed", 
                ["Dry-run: no preview command defined for this action"], 
                [], null);
            _logger.LogInformation("Dry-run {ActionId}: no preview available", actionId);
            return _runStore.GetRun(runId)!;
        }

        CommandResult result;
        try
        {
            result = await _executor.ExecuteAsync(executable!, argv!, action.TimeoutSeconds, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execution failed for {ActionId} run {RunId}", actionId, runId);
            _runStore.UpdateRun(runId, -1, "failed", [], [], ex.Message);
            return _runStore.GetRun(runId)!;
        }

        // 9. Update run with execution results
        var finalStatus = isDryRun ? "completed" : (result.ExitCode == 0 ? "completed" : "failed");
        if (result.ErrorMessage is not null)
            finalStatus = "failed";

        _runStore.UpdateRun(runId, result.ExitCode, finalStatus, result.StdoutLines, result.StderrLines, result.ErrorMessage);
        _logger.LogInformation("FleetOps run {RunId} ({ActionId}): exit={ExitCode}, status={Status}",
            runId, actionId, result.ExitCode, finalStatus);

        return _runStore.GetRun(runId)!;
    }

    /// <summary>Look up a run by ID.</summary>
    public FleetOpsActionRun? GetRun(string runId) => _runStore.GetRun(runId);

    private static FleetActionDescriptor BuildDescriptor(FleetOpsAction action)
    {
        return new FleetActionDescriptor(
            ActionId: action.Id,
            Label: action.Label,
            RiskLevel: action.RiskLevel,
            Mutating: action.Mutating,
            SupportsDryRun: action.SupportsDryRun,
            NeedsConfirmation: action.NeedsConfirmation,
            ConfirmationCopy: action.ConfirmationCopy,
            TimeoutSeconds: action.TimeoutSeconds,
            ArgsSchema: action.ArgsSchema.Count > 0 ? action.ArgsSchema : null,
            DisabledReason: action.DisabledReason);
    }

    private static FleetOpsActionRun CreateErrorRun(string runId, string actionId, FleetOpsActionRunRequest request, DateTimeOffset now, string error)
    {
        return new FleetOpsActionRun(
            RunId: runId,
            ActionId: actionId,
            Args: request.Args ?? new Dictionary<string, string>(),
            Status: "failed",
            CreatedAt: now,
            FinishedAt: now,
            ErrorMessage: error);
    }

    private static string? ValidateArgs(FleetOpsAction action, IReadOnlyDictionary<string, string> args)
    {
        foreach (var schema in action.ArgsSchema)
        {
            var hasValue = args.TryGetValue(schema.Name, out var value) && !string.IsNullOrWhiteSpace(value);

            if (schema.Required && !hasValue)
                return $"Required argument '{schema.Name}' is missing";

            if (hasValue && !string.IsNullOrWhiteSpace(schema.Pattern))
            {
                if (!Regex.IsMatch(value!, schema.Pattern))
                    return $"Argument '{schema.Name}' value '{value}' does not match required pattern: {schema.Pattern}";
            }
        }

        // Reject unknown args
        var allowedNames = new HashSet<string>(action.ArgsSchema.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var key in args.Keys)
        {
            if (!allowedNames.Contains(key))
                return $"Unknown argument '{key}' not allowed for action '{action.Id}'";
        }

        return null;
    }

    /// <summary>
    /// Build the resolved executable path and fixed argv from a typed action definition.
    /// No API path/body value becomes an executable path — only registry data.
    /// For systemctl-based actions, the executable is always the configured SystemctlPath
    /// (e.g. "systemctl") and the template provides the sub-command arguments
    /// (e.g. "--user restart hermes-gateway@{profile}.service").
    /// </summary>
    internal (string Executable, string[] Args)? BuildCommand(FleetOpsAction action, IReadOnlyDictionary<string, string> args, bool isDryRun)
    {
        // For systemctl-based actions
        if (action.SystemctlTemplate is not null)
        {
            var template = isDryRun && action.DryRunSystemctlTemplate is not null
                ? action.DryRunSystemctlTemplate
                : action.SystemctlTemplate;

            var command = SubstituteTemplate(template, args);
            if (command is null) return null;

            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 1) return null;

            // The executable is the configured systemctl binary (e.g. "systemctl"),
            // NOT the first token from the template (which would be a sub-command like "restart").
            var argv = new List<string>();
            argv.Add("--user");
            foreach (var part in parts)
                argv.Add(part);

            return (_options.SystemctlPath, argv.ToArray());
        }

        // For script-based actions
        if (action.ScriptPath is not null)
        {
            var scriptName = isDryRun && action.DryRunScriptPath is not null
                ? action.DryRunScriptPath
                : action.ScriptPath;

            // Resolve script names from the configured ScriptsDirectory.
            // Only the registry-defined script names are used; no API input reaches here.
            var resolvedScriptPath = scriptName.Contains('/') || scriptName.Contains('\\')
                ? scriptName
                : Path.Combine(_options.ScriptsDirectory, scriptName);

            var argv = new List<string>();

            // For restart-agent-services, add fixed args based on action
            if (action.Id == "restart-all")
            {
                if (!isDryRun)
                    argv.Add("--yes");
            }
            else if (action.Id == "restart-failed")
            {
                if (!isDryRun)
                {
                    argv.Add("--yes");
                    argv.Add("--failed-only");
                }
            }

            return (resolvedScriptPath, argv.ToArray());
        }

        return null;
    }

    /// <summary>
    /// Substitute {argName} placeholders in a template with validated argument values.
    /// Accepts only the validated args; no string interpolation of arbitrary input.
    /// </summary>
    private static string? SubstituteTemplate(string template, IReadOnlyDictionary<string, string> args)
    {
        // Replace each {argName} with its value from the validated args dict
        // Only args known to the schema are present in args (validated upstream)
        var result = template;
        foreach (var kvp in args)
        {
            result = result.Replace($"{{{kvp.Key}}}", kvp.Value);
        }

        // If any placeholders remain unresolved, fail
        if (result.Contains('{') && result.Contains('}'))
            return null;

        return result;
    }

    private static bool IsConfirmationValid(string confirmation, FleetOpsAction action)
    {
        // Simple confirmation: "yes", "true", "confirm", or the exact confirmation copy
        if (string.IsNullOrWhiteSpace(confirmation))
            return false;

        var lower = confirmation.Trim().ToLowerInvariant();
        return lower is "yes" or "true" or "confirm" or "confirmed" ||
               (action.ConfirmationCopy is not null &&
                string.Equals(confirmation.Trim(), action.ConfirmationCopy, StringComparison.Ordinal));
    }
}
