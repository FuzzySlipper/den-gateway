using System.Diagnostics;

namespace DenGateway.Service.FleetOps;

/// <summary>
/// Abstraction for executing commands against the fleet scripts directory.
/// Only registry/service code builds executable + argv from typed actions.
/// </summary>
public interface IFleetOpsCommandExecutor
{
    /// <summary>
    /// Execute a resolved command with fixed arguments.
    /// Captures stdout/stderr, enforces timeout, redacts secrets.
    /// </summary>
    Task<CommandResult> ExecuteAsync(string executable, string[] args, int timeoutSeconds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes commands using System.Diagnostics.Process against a configured script directory.
/// Only accepts resolved executable paths and fixed argv from typed action definitions.
/// </summary>
public sealed class ProcessFleetOpsCommandExecutor : IFleetOpsCommandExecutor
{
    private readonly FleetOpsOptions _options;
    private readonly ILogger<ProcessFleetOpsCommandExecutor> _logger;

    public ProcessFleetOpsCommandExecutor(FleetOpsOptions options, ILogger<ProcessFleetOpsCommandExecutor> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CommandResult> ExecuteAsync(string executable, string[] args, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Executable cannot be empty", nameof(executable));

        // If executable is a script name (no path separators), resolve from script directory
        var resolvedPath = executable.Contains('/') || executable.Contains('\\')
            ? executable
            : Path.Combine(_options.ScriptsDirectory, executable);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var psi = new ProcessStartInfo
            {
                FileName = resolvedPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new CommandResult(-1, Array.Empty<string>(), Array.Empty<string>(),
                    $"Failed to start process: {resolvedPath}");
            }

            var stdoutLines = new List<string>();
            var stderrLines = new List<string>();

            // Read stdout and stderr concurrently
            var stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, cts.Token);
            var stderrTask = ReadLinesAsync(process.StandardError, stderrLines, cts.Token);

            await Task.WhenAll(stdoutTask, stderrTask);

            // Wait for exit with timeout
            var exited = process.WaitForExit(timeoutSeconds * 1000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new CommandResult(-1,
                    FleetOpsSecretRedactor.ProcessOutput(stdoutLines, _options.MaxOutputLines),
                    FleetOpsSecretRedactor.ProcessOutput(stderrLines, _options.MaxOutputLines),
                    $"Process timed out after {timeoutSeconds}s");
            }

            return new CommandResult(
                process.ExitCode,
                FleetOpsSecretRedactor.ProcessOutput(stdoutLines, _options.MaxOutputLines),
                FleetOpsSecretRedactor.ProcessOutput(stderrLines, _options.MaxOutputLines));
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(-1, Array.Empty<string>(), Array.Empty<string>(), "Execution was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command execution failed: {Executable}", resolvedPath);
            return new CommandResult(-1, Array.Empty<string>(), Array.Empty<string>(), ex.Message);
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, List<string> lines, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                lines.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout
        }
    }
}

/// <summary>
/// Stub executor for tests that returns configurable results without shelling out.
/// </summary>
public sealed class StubFleetOpsCommandExecutor : IFleetOpsCommandExecutor
{
    private readonly Func<string, string[], int, CancellationToken, Task<CommandResult>> _handler;

    public StubFleetOpsCommandExecutor(Func<string, string[], int, CancellationToken, Task<CommandResult>>? handler = null)
    {
        _handler = handler ?? ((_, _, _, _) => Task.FromResult(new CommandResult(0, Array.Empty<string>(), Array.Empty<string>())));
    }

    public Task<CommandResult> ExecuteAsync(string executable, string[] args, int timeoutSeconds, CancellationToken cancellationToken = default)
        => _handler(executable, args, timeoutSeconds, cancellationToken);
}
