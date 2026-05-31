using System.Collections.Concurrent;

namespace DenGateway.Service.FleetOps;

/// <summary>
/// Abstraction for storing and retrieving fleet action runs.
/// </summary>
public interface IFleetOpsRunStore
{
    /// <summary>Store a new run.</summary>
    void AddRun(FleetOpsActionRun run);

    /// <summary>Get a run by ID, or null if not found.</summary>
    FleetOpsActionRun? GetRun(string runId);

    /// <summary>Get the most recent runs, up to limit.</summary>
    IReadOnlyList<FleetOpsActionRun> GetRecentRuns(int limit = 20);

    /// <summary>Update run status fields after execution starts.</summary>
    void UpdateRunStarted(string runId);

    /// <summary>Update run status fields after execution completes.</summary>
    void UpdateRun(string runId, int? exitCode, string status, IReadOnlyList<string>? stdoutTail, IReadOnlyList<string>? stderrTail, string? errorMessage);
}

/// <summary>
/// In-memory bounded run store with LRU-style eviction. Thread-safe.
/// Acceptable for v1; designed for easy SQLite replacement.
/// </summary>
public sealed class InMemoryFleetOpsRunStore : IFleetOpsRunStore
{
    private readonly ConcurrentDictionary<string, FleetOpsActionRun> _runs = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _maxRuns;
    private readonly object _evictionLock = new();

    public InMemoryFleetOpsRunStore(int maxRuns = 1000)
    {
        _maxRuns = maxRuns;
    }

    public void AddRun(FleetOpsActionRun run)
    {
        if (_runs.TryAdd(run.RunId, run))
        {
            _order.Enqueue(run.RunId);
            EvictIfNeeded();
        }
    }

    public FleetOpsActionRun? GetRun(string runId)
    {
        _runs.TryGetValue(runId, out var run);
        return run;
    }

    public IReadOnlyList<FleetOpsActionRun> GetRecentRuns(int limit = 20)
    {
        var result = new List<FleetOpsActionRun>();
        // Walk the queue from newest to oldest
        var snapshot = _order.ToArray();
        for (int i = snapshot.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            if (_runs.TryGetValue(snapshot[i], out var run))
                result.Add(run);
        }
        return result.AsReadOnly();
    }

    public void UpdateRun(string runId, int? exitCode, string status, IReadOnlyList<string>? stdoutTail, IReadOnlyList<string>? stderrTail, string? errorMessage)
    {
        if (!_runs.TryGetValue(runId, out var existing))
            return;

        var updated = existing with
        {
            Status = status,
            ExitCode = exitCode,
            FinishedAt = DateTimeOffset.UtcNow,
            StdoutTail = stdoutTail,
            StderrTail = stderrTail,
            ErrorMessage = errorMessage
        };

        _runs.TryUpdate(runId, updated, existing);
    }

    public void UpdateRunStarted(string runId)
    {
        if (!_runs.TryGetValue(runId, out var existing))
            return;

        var updated = existing with
        {
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow
        };

        _runs.TryUpdate(runId, updated, existing);
    }

    private void EvictIfNeeded()
    {
        lock (_evictionLock)
        {
            while (_runs.Count > _maxRuns && _order.TryDequeue(out var oldestId))
            {
                _runs.TryRemove(oldestId, out _);
            }
        }
    }
}
