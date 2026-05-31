namespace DenGateway.Service.FleetOps;

/// <summary>
/// Abstraction for discovering Hermes gateway systemd service units.
/// </summary>
public interface IFleetOpsServiceUnitDiscovery
{
    /// <summary>Discover service units. Returns empty list on failure with diagnostics.</summary>
    Task<(IReadOnlyList<FleetServiceUnit> Units, string? Diagnostics)> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovers service units by querying systemctl --user for hermes-gateway@* services.
/// </summary>
public sealed class SystemdFleetOpsDiscovery : IFleetOpsServiceUnitDiscovery
{
    private readonly FleetOpsOptions _options;
    private readonly ILogger<SystemdFleetOpsDiscovery> _logger;

    public SystemdFleetOpsDiscovery(FleetOpsOptions options, ILogger<SystemdFleetOpsDiscovery> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(IReadOnlyList<FleetServiceUnit> Units, string? Diagnostics)> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var units = new List<FleetServiceUnit>();

            // Try --user manager first
            var (userUnits, userDiag) = await QuerySystemctlAsync("--user", cancellationToken);
            units.AddRange(userUnits);
            if (units.Count > 0)
                return (units.AsReadOnly(), null);

            // Fallback: try system manager
            var (systemUnits, systemDiag) = await QuerySystemctlAsync("--system", cancellationToken);
            units.AddRange(systemUnits);
            if (units.Count > 0)
                return (units.AsReadOnly(), "Discovered via --system (user manager unavailable)");

            var diag = userDiag ?? systemDiag ?? "No hermes-gateway@ services found";
            _logger.LogWarning("Service discovery failed: {Diagnostics}", diag);
            return (Array.Empty<FleetServiceUnit>(), diag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Service discovery threw exception");
            return (Array.Empty<FleetServiceUnit>(), $"Discovery error: {ex.Message}");
        }
    }

    private async Task<(List<FleetServiceUnit> Units, string? Diagnostics)> QuerySystemctlAsync(string mode, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _options.SystemctlPath,
                Arguments = $"{mode} list-units 'hermes-gateway@*' --no-legend --no-pager",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return (new List<FleetServiceUnit>(), "Failed to start systemctl process");

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return (new List<FleetServiceUnit>(), $"systemctl {mode} exit code {process.ExitCode}: {Truncate(stderr, 200)}");

            var units = ParseSystemctlOutput(stdout, mode == "--user");
            return (units, null);
        }
        catch (Exception ex)
        {
            return (new List<FleetServiceUnit>(), $"systemctl {mode} error: {ex.Message}");
        }
    }

    private static List<FleetServiceUnit> ParseSystemctlOutput(string output, bool isUserManager)
    {
        var units = new List<FleetServiceUnit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // systemctl list-units output: UNIT LOAD ACTIVE SUB DESCRIPTION
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
                continue;

            var unitName = parts[0];
            var activeState = parts[2];
            var subState = parts[3];

            // Extract profile name from unit name: hermes-gateway@spawned-coder.service -> spawned-coder
            var profileName = ExtractProfileName(unitName);
            if (profileName is null)
                continue;

            units.Add(new FleetServiceUnit(
                UnitName: unitName,
                ProfileName: profileName,
                ActiveState: activeState,
                SubState: subState));
        }
        return units;
    }

    private static string? ExtractProfileName(string unitName)
    {
        // Match patterns: hermes-gateway@spawned-coder.service, hermes-gateway@runner.service
        var atIndex = unitName.IndexOf('@');
        if (atIndex < 0) return null;

        var afterAt = unitName[(atIndex + 1)..];
        var dotIndex = afterAt.LastIndexOf('.');
        return dotIndex > 0 ? afterAt[..dotIndex] : afterAt;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}

/// <summary>
/// Stub implementation for tests returning configurable known units.
/// </summary>
public sealed class StubFleetOpsDiscovery : IFleetOpsServiceUnitDiscovery
{
    private readonly IReadOnlyList<FleetServiceUnit> _units;
    private readonly string? _diagnostics;
    private readonly bool _simulateFailure;

    public StubFleetOpsDiscovery(IReadOnlyList<FleetServiceUnit>? units = null, string? diagnostics = null, bool simulateFailure = false)
    {
        _units = units ?? Array.Empty<FleetServiceUnit>();
        _diagnostics = diagnostics;
        _simulateFailure = simulateFailure;
    }

    public Task<(IReadOnlyList<FleetServiceUnit> Units, string? Diagnostics)> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (_simulateFailure)
            return Task.FromResult<(IReadOnlyList<FleetServiceUnit> Units, string? Diagnostics)>((Array.Empty<FleetServiceUnit>(), _diagnostics ?? "Simulated discovery failure"));

        return Task.FromResult((_units!, _diagnostics));
    }
}

/// <summary>
/// No-op discovery for scenarios where live discovery is not needed.
/// </summary>
public sealed class NoopFleetOpsDiscovery : IFleetOpsServiceUnitDiscovery
{
    public Task<(IReadOnlyList<FleetServiceUnit> Units, string? Diagnostics)> DiscoverAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<(IReadOnlyList<FleetServiceUnit> Units, string? Diagnostics)>((Array.Empty<FleetServiceUnit>(), (string?)null));
}
