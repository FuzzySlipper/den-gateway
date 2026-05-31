using DenGateway.Service.FleetOps;
using Microsoft.Extensions.Logging;

namespace DenGateway.Service.Tests;

public class FleetOpsTests
{
    // ===================== Action Registry Tests =====================

    [Fact]
    public void ActionRegistry_RejectsUnknownActionId()
    {
        var registry = new FleetOpsActionRegistry();
        var action = registry.GetById("nonexistent-action");
        Assert.Null(action);
    }

    [Fact]
    public void ActionRegistry_ReturnsKnownActions()
    {
        var registry = new FleetOpsActionRegistry();
        Assert.NotNull(registry.GetById("fleet-status"));
        Assert.NotNull(registry.GetById("fleet-smoke"));
        Assert.NotNull(registry.GetById("restart-all"));
        Assert.NotNull(registry.GetById("restart-failed"));
        Assert.NotNull(registry.GetById("restart-profile"));
    }

    [Fact]
    public void ActionRegistry_DisabledActionsHaveReason()
    {
        var registry = new FleetOpsActionRegistry();
        foreach (var id in new[] { "fleet-update", "deploy-skills", "propagate-auth", "archive-launchers" })
        {
            var action = registry.GetById(id);
            Assert.NotNull(action);
            Assert.False(string.IsNullOrEmpty(action.DisabledReason),
                $"Action '{id}' should have a disabledReason");
        }
    }

    [Fact]
    public void ActionRegistry_RunnableActionsHaveNoDisabledReason()
    {
        var registry = new FleetOpsActionRegistry();
        foreach (var id in new[] { "fleet-status", "fleet-smoke", "restart-all", "restart-failed", "restart-profile" })
        {
            var action = registry.GetById(id);
            Assert.NotNull(action);
            Assert.Null(action.DisabledReason);
        }
    }

    [Fact]
    public void ActionRegistry_RestartAllNeedsConfirmation()
    {
        var registry = new FleetOpsActionRegistry();
        var action = registry.GetById("restart-all");
        Assert.NotNull(action);
        Assert.True(action.NeedsConfirmation);
        Assert.NotNull(action.ConfirmationCopy);
    }

    // ===================== Profile Validation Tests =====================

    [Theory]
    [InlineData("spawned-coder")]
    [InlineData("runner")]
    [InlineData("den-mcp-runner")]
    [InlineData("default")]
    [InlineData("a")]
    public void ProfileArg_AcceptsValidProfileNames(string profile)
    {
        var regex = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_-]+$");
        Assert.True(regex.IsMatch(profile));
    }

    [Theory]
    [InlineData("../")]
    [InlineData("../../etc")]
    [InlineData("spawned-coder; rm -rf /")]
    [InlineData("spawned-coder && echo pwned")]
    [InlineData("$(cat /etc/passwd)")]
    [InlineData("`id`")]
    [InlineData("")]
    [InlineData("spawned coder")]
    [InlineData("spawned.coder")]
    public void ProfileArg_RejectsInvalidProfileNames(string profile)
    {
        var regex = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_-]+$");
        Assert.False(regex.IsMatch(profile));
    }

    // ===================== Action Registry Validation Tests =====================

    [Fact]
    public void ActionRegistry_RestartProfileHasProfileArgSchema()
    {
        var registry = new FleetOpsActionRegistry();
        var action = registry.GetById("restart-profile");
        Assert.NotNull(action);
        Assert.NotEmpty(action.ArgsSchema);
        var profileArg = action.ArgsSchema[0];
        Assert.Equal("profile", profileArg.Name);
        Assert.True(profileArg.Required);
        Assert.Equal("^[a-zA-Z0-9_-]+$", profileArg.Pattern);
    }

    [Fact]
    public void ActionRegistry_NonProfileActionsHaveNoArgs()
    {
        var registry = new FleetOpsActionRegistry();
        foreach (var id in new[] { "fleet-status", "fleet-smoke", "restart-all", "restart-failed" })
        {
            var action = registry.GetById(id);
            Assert.NotNull(action);
            Assert.Empty(action.ArgsSchema);
        }
    }

    // ===================== Service Overview Tests =====================

    [Fact]
    public async Task ServiceOverview_ReturnsUnitListAndActions()
    {
        var units = new List<FleetServiceUnit>
        {
            new("hermes-gateway@spawned-coder.service", "spawned-coder", "active", "running"),
            new("hermes-gateway@runner.service", "runner", "active", "running")
        };

        var service = CreateService(units: units);
        var result = await service.GetOverviewAsync();

        Assert.Equal("den-gateway", result.Service);
        Assert.Equal(2, result.ServiceUnits.Count);
        Assert.Equal("spawned-coder", result.ServiceUnits[0].ProfileName);
        Assert.Equal("runner", result.ServiceUnits[1].ProfileName);

        // Should include all 9 actions (5 runnable + 4 disabled)
        Assert.Equal(9, result.Actions.Count);
        Assert.Contains(result.Actions, a => a.ActionId == "fleet-status" && !a.Mutating);
        Assert.Contains(result.Actions, a => a.ActionId == "restart-all" && a.Mutating && a.NeedsConfirmation);
        Assert.Contains(result.Actions, a => a.ActionId == "fleet-update" && a.DisabledReason != null);
    }

    [Fact]
    public async Task ServiceOverview_FailedDiscovery_ReturnsEmptyUnitsWithDiagnostics()
    {
        var service = CreateService(simulateDiscoveryFailure: true);
        var result = await service.GetOverviewAsync();

        Assert.Empty(result.ServiceUnits);
        Assert.NotNull(result.DiscoveryDiagnostics);
        Assert.Contains("failure", result.DiscoveryDiagnostics, StringComparison.OrdinalIgnoreCase);
        // Actions should still be returned even when discovery fails
        Assert.NotEmpty(result.Actions);
    }

    [Fact]
    public async Task ServiceOverview_RecentRunsIncluded()
    {
        var service = CreateService();
        var request = new FleetOpsActionRunRequest("restart-all", DryRun: false, Args: null, Confirmation: "yes");
        await service.ExecuteActionAsync("restart-all", request);

        var result = await service.GetOverviewAsync();
        Assert.NotNull(result.RecentRuns);
        Assert.Single(result.RecentRuns);
        Assert.Equal("restart-all", result.RecentRuns[0].ActionId);
    }

    // ===================== Action Execution Tests =====================

    [Fact]
    public async Task ActionExecute_RejectsUnknownAction()
    {
        var service = CreateService();
        var request = new FleetOpsActionRunRequest("nonexistent");
        var result = await service.ExecuteActionAsync("nonexistent", request);

        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Unknown action", result.ErrorMessage);
    }

    [Fact]
    public async Task ActionExecute_RejectsDisabledAction()
    {
        var service = CreateService();
        var request = new FleetOpsActionRunRequest("fleet-update");
        var result = await service.ExecuteActionAsync("fleet-update", request);

        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("disabled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActionExecute_RequiresConfirmationForRestartAll()
    {
        var service = CreateService();
        var request = new FleetOpsActionRunRequest("restart-all", DryRun: false, Args: null, Confirmation: null);
        var result = await service.ExecuteActionAsync("restart-all", request);

        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Confirmation", result.ErrorMessage);
    }

    [Fact]
    public async Task ActionExecute_AcceptValidConfirmationForRestartAll()
    {
        var recordedDir = "";
        var recordedArgs = Array.Empty<string>();

        var service = CreateService(
            executorHandler: async (exec, args, _, _) =>
            {
                recordedDir = exec;
                recordedArgs = args;
                return new CommandResult(0, ["Restart completed"], [], null);
            },
            units: [new("hermes-gateway@spawned-coder.service", "spawned-coder", "active", "running")]);

        var request = new FleetOpsActionRunRequest("restart-all", DryRun: false, Args: null, Confirmation: "yes");
        var result = await service.ExecuteActionAsync("restart-all", request);

        Assert.Equal("completed", result.Status);
        Assert.Equal("restart-agent-services", recordedDir);
        Assert.Contains("--yes", recordedArgs);
    }

    [Fact]
    public async Task ActionExecute_DryRunDoesNotInvokeMutatingArgv()
    {
        var recordedArgs = Array.Empty<string>();

        var service = CreateService(
            executorHandler: async (exec, args, _, _) =>
            {
                recordedArgs = args;
                return new CommandResult(0, ["status ok"], [], null);
            },
            units: [new("hermes-gateway@spawned-coder.service", "spawned-coder", "active", "running")]);

        var request = new FleetOpsActionRunRequest("restart-all", DryRun: true, Args: null, Confirmation: "yes");
        var result = await service.ExecuteActionAsync("restart-all", request);

        Assert.Equal("completed", result.Status);
        Assert.True(result.WasDryRun);
        // Dry-run should NOT include --yes
        Assert.DoesNotContain("--yes", recordedArgs);
    }

    [Fact]
    public async Task ActionExecute_NonMutatingActionsExecuteOnDryRun()
    {
        var executed = false;
        var service = CreateService(
            executorHandler: async (exec, args, _, _) =>
            {
                executed = true;
                return new CommandResult(0, ["status: all services running"], [], null);
            });

        var request = new FleetOpsActionRunRequest("fleet-status", DryRun: true);
        var result = await service.ExecuteActionAsync("fleet-status", request);

        Assert.True(executed, "Non-mutating actions should execute even in dry-run mode");
        Assert.Equal("completed", result.Status);
    }

    // ===================== Output Redaction Tests =====================

    [Theory]
    [InlineData("Bearer sk-pro...7890", "[REDACTED]")]
    [InlineData("API_KEY=sk-abcdef1234567890", "[REDACTED]")]
    [InlineData("token: ghp_abcdef1234567890abcdef123456", "[REDACTED]")]
    [InlineData("eyJhbG...8qkA", "[REDACTED]")]
    [InlineData("normal output line", "normal output line")]
    [InlineData("", "")]
    public void OutputRedaction_RedactsTokensAndKeys(string input, string expected)
    {
        var redacted = FleetOpsSecretRedactor.RedactLine(input);
        Assert.Equal(expected, redacted);
    }

    [Fact]
    public void OutputTruncation_CapsAtMaxLines()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToList();
        var result = FleetOpsSecretRedactor.ProcessOutput(lines, 50);

        Assert.Equal(50, result.Count);
        Assert.Equal("line 1", result[0]);
        Assert.Equal("line 50", result[^1]);
    }

    [Fact]
    public void OutputTruncation_DoesNotExpandSmallOutput()
    {
        var lines = new[] { "a", "b", "c" };
        var result = FleetOpsSecretRedactor.ProcessOutput(lines, 100);

        Assert.Equal(3, result.Count);
    }

    // ===================== Run Readback Tests =====================

    [Fact]
    public async Task RunReadback_ShapeMatchesContract()
    {
        var service = CreateService(
            executorHandler: async (_, _, _, _) =>
                new CommandResult(0, ["output line 1", "output line 2"], ["stderr line"], null),
            units: [new("hermes-gateway@spawned-coder.service", "spawned-coder", "active", "running")]);

        var request = new FleetOpsActionRunRequest("fleet-smoke", DryRun: false);
        var run = await service.ExecuteActionAsync("fleet-smoke", request);

        Assert.NotNull(run);
        Assert.False(string.IsNullOrEmpty(run.RunId));
        Assert.Equal("fleet-smoke", run.ActionId);
        Assert.Equal("completed", run.Status);
        Assert.NotNull(run.CreatedAt);
        Assert.NotNull(run.StartedAt);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.StdoutTail);
        Assert.NotEmpty(run.StdoutTail);
        Assert.NotNull(run.StderrTail);
    }

    [Fact]
    public async Task RunReadback_GetRunByIdReturnsRun()
    {
        var service = CreateService(units: [new("hermes-gateway@runner.service", "runner", "active", "running")]);
        var request = new FleetOpsActionRunRequest("fleet-status");
        var run = await service.ExecuteActionAsync("fleet-status", request);

        var fetched = service.GetRun(run.RunId);
        Assert.NotNull(fetched);
        Assert.Equal(run.RunId, fetched.RunId);
        Assert.Equal(run.ActionId, fetched.ActionId);
        Assert.Equal(run.Status, fetched.Status);
    }

    [Fact]
    public async Task RunReadback_GetUnknownRunReturnsNull()
    {
        var service = CreateService();
        var run = service.GetRun("nonexistent-run-id");
        Assert.Null(run);
    }

    [Fact]
    public async Task ActionExecute_StoresFailedRunWithError()
    {
        var service = CreateService(
            executorHandler: async (_, _, _, _) =>
                new CommandResult(1, ["before error"], ["Error: something broke"], "something broke"),
            units: [new("hermes-gateway@spawned-coder.service", "spawned-coder", "active", "running")]);

        var request = new FleetOpsActionRunRequest("fleet-status");
        var result = await service.ExecuteActionAsync("fleet-status", request);

        Assert.Equal("failed", result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.StderrTail);
        Assert.Contains(result.StderrTail, l => l.Contains("Error"));
    }

    // ===================== Endpoint Shape Tests (via Service) =====================

    [Fact]
    public async Task ActionExecute_BadRequestForFailedAction()
    {
        // Simulate the endpoint logic: failed status -> BadRequest
        var service = CreateService();
        var request = new FleetOpsActionRunRequest("nonexistent");
        var result = await service.ExecuteActionAsync("nonexistent", request);

        Assert.Equal("failed", result.Status);

        // Verify the 400-response shape (the endpoint returns Run directly)
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal("nonexistent", result.ActionId);
        Assert.NotNull(result.RunId);
    }

    [Fact]
    public async Task ActionExecute_OkForSuccessfulAction()
    {
        var service = CreateService(
            executorHandler: async (_, _, _, _) => new CommandResult(0, ["OK"], [], null),
            units: [new("hermes-gateway@spawned-coder.service", "spawned-coder", "active", "running")]);

        var request = new FleetOpsActionRunRequest("fleet-smoke");
        var result = await service.ExecuteActionAsync("fleet-smoke", request);

        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ActionExecute_DisabledActionReturnsBadRequest()
    {
        var service = CreateService();
        var request = new FleetOpsActionRunRequest("deploy-skills");
        var result = await service.ExecuteActionAsync("deploy-skills", request);

        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("disabled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ===================== Dry-Run Semantics Tests =====================

    [Fact]
    public async Task DryRun_RestartFailedDoesNotIncludeYes()
    {
        var recordedArgs = Array.Empty<string>();
        var service = CreateService(
            executorHandler: async (exec, args, _, _) =>
            {
                recordedArgs = args;
                return new CommandResult(0, ["status check"], [], null);
            });

        var request = new FleetOpsActionRunRequest("restart-failed", DryRun: true);
        var result = await service.ExecuteActionAsync("restart-failed", request);

        Assert.Equal("completed", result.Status);
        Assert.True(result.WasDryRun);
        // Dry-run restart-failed should not invoke --yes or --failed-only
        Assert.DoesNotContain("--yes", recordedArgs);
        Assert.DoesNotContain("--failed-only", recordedArgs);
    }

    // ===================== Private Helpers =====================

    private static FleetOpsService CreateService(
        IReadOnlyList<FleetServiceUnit>? units = null,
        bool simulateDiscoveryFailure = false,
        Func<string, string[], int, CancellationToken, Task<CommandResult>>? executorHandler = null)
    {
        var options = new FleetOpsOptions
        {
            ScriptsDirectory = "/home/agents/local/hermes-fleet/bin",
            MaxOutputLines = 100,
            MaxRuns = 1000,
            DefaultTimeoutSeconds = 60
        };

        var registry = new FleetOpsActionRegistry();
        var discovery = new StubFleetOpsDiscovery(units, simulateFailure: simulateDiscoveryFailure);
        var executor = new StubFleetOpsCommandExecutor(executorHandler);
        var runStore = new InMemoryFleetOpsRunStore(options.MaxRuns);
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });

        return new FleetOpsService(
            registry,
            discovery,
            executor,
            runStore,
            options,
            loggerFactory.CreateLogger<FleetOpsService>());
    }
}
