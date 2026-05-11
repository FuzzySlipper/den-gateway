namespace DenGateway.Service.Sentinel;

public sealed class SentinelStateMachine
{
    private readonly SentinelSettings _settings;

    public SentinelStateMachine(SentinelSettings settings)
    {
        _settings = settings;
    }

    public SentinelTransitionResult StartMaintenance(string reason, string requestedBy, IReadOnlyList<SentinelBinding> activeBindings, DateTimeOffset now)
    {
        var maintenanceId = $"maintenance-{now:yyyyMMddHHmmss}";
        var messages = activeBindings
            .Select(binding => ControlMessage("pause", "planned_maintenance", maintenanceId, binding, now, PauseInstructions()))
            .ToArray();
        var events = new List<SentinelEvent>
        {
            new("maintenance_notice_received", null, $"{{\"reason\":\"{Escape(reason)}\",\"requested_by\":\"{Escape(requestedBy)}\"}}", now)
        };
        events.AddRange(messages.Select(message => new SentinelEvent("pause_sent", message.TargetIdentity, "{}", now)));

        return new SentinelTransitionResult(
            State: "paused_den_maintenance",
            RuntimeState: new SentinelRuntimeState("paused_den_maintenance", 0, 0, maintenanceId),
            ControlMessages: messages,
            Events: events);
    }

    public SentinelTransitionResult ApplyHealthProbe(SentinelRuntimeState current, HealthProbeResult probe, IReadOnlyList<SentinelBinding> activeBindings, DateTimeOffset now)
    {
        if (probe.IsReady)
        {
            return ApplySuccessfulProbe(current, activeBindings, now);
        }

        return ApplyFailedProbe(current, activeBindings, now, probe.ErrorCode ?? "unavailable");
    }

    private SentinelTransitionResult ApplyFailedProbe(SentinelRuntimeState current, IReadOnlyList<SentinelBinding> activeBindings, DateTimeOffset now, string errorCode)
    {
        var failures = current.FailureCount + 1;
        if (failures >= _settings.DownFailureThreshold)
        {
            var outageId = current.CorrelationId ?? $"outage-{now:yyyyMMddHHmmss}";
            var messages = activeBindings
                .Select(binding => ControlMessage("pause", "den_unreachable", outageId, binding, now, PauseInstructions()))
                .ToArray();
            var events = new List<SentinelEvent> { new("down_detected", null, $"{{\"error\":\"{Escape(errorCode)}\"}}", now) };
            events.AddRange(messages.Select(message => new SentinelEvent("pause_sent", message.TargetIdentity, "{}", now)));
            return new SentinelTransitionResult(
                "down_detected",
                new SentinelRuntimeState("down_detected", failures, 0, outageId),
                messages,
                events);
        }

        if (failures >= _settings.DegradedFailureThreshold)
        {
            return new SentinelTransitionResult(
                "degraded",
                new SentinelRuntimeState("degraded", failures, 0, current.CorrelationId),
                [],
                [new SentinelEvent("health_degraded", null, $"{{\"error\":\"{Escape(errorCode)}\"}}", now)]);
        }

        return new SentinelTransitionResult(
            current.State,
            current with { FailureCount = failures, SuccessCount = 0 },
            [],
            []);
    }

    private SentinelTransitionResult ApplySuccessfulProbe(SentinelRuntimeState current, IReadOnlyList<SentinelBinding> activeBindings, DateTimeOffset now)
    {
        if (current.State is "down_detected" or "degraded" or "waiting_for_stable" or "paused_den_maintenance")
        {
            var successes = current.SuccessCount + 1;
            if (successes >= _settings.StableSuccessThreshold)
            {
                var correlationId = current.CorrelationId ?? $"recovery-{now:yyyyMMddHHmmss}";
                var messages = activeBindings
                    .Select(binding => ControlMessage("resume", "den_stable", correlationId, binding, now, ResumeInstructions()))
                    .ToArray();
                var events = messages.Select(message => new SentinelEvent("resume_sent", message.TargetIdentity, "{}", now)).ToArray();
                return new SentinelTransitionResult(
                    "normal_after_resume",
                    new SentinelRuntimeState("normal_after_resume", 0, successes, correlationId),
                    messages,
                    events);
            }

            return new SentinelTransitionResult(
                "waiting_for_stable",
                new SentinelRuntimeState("waiting_for_stable", 0, successes, current.CorrelationId),
                [],
                []);
        }

        return new SentinelTransitionResult(
            "normal",
            new SentinelRuntimeState("normal", 0, current.SuccessCount + 1, current.CorrelationId),
            [],
            []);
    }

    private ControlMessage ControlMessage(string controlKind, string reason, string correlationId, SentinelBinding binding, DateTimeOffset now, IReadOnlyList<string> instructions)
    {
        var dedupePrefix = controlKind == "pause" ? "den-pause" : "den-resume";
        return new ControlMessage(
            Type: "den_control",
            ControlKind: controlKind,
            Reason: reason,
            Scope: "agent",
            ScopeId: binding.TargetIdentity,
            TargetIdentity: binding.TargetIdentity,
            SentinelId: _settings.SentinelId,
            EventId: Guid.NewGuid().ToString("N"),
            IssuedAt: now,
            Instructions: instructions,
            AckRequested: true,
            DedupeKey: $"{dedupePrefix}:{correlationId}:{binding.TargetIdentity}");
    }

    private static string[] PauseInstructions() =>
    [
        "Do not start new Den-dependent work.",
        "Do not guess from stale Den state.",
        "Preserve local in-flight state if safe.",
        "Enter holding pattern until resume/all-clear.",
        "On resume, refresh Den state before continuing."
    ];

    private static string[] ResumeInstructions() =>
    [
        "Refresh Den state before continuing.",
        "Re-check task assignment, task status, dependencies, and review state.",
        "Resume only work still valid in Den.",
        "If local state conflicts with Den, stop and ask."
    ];

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

public sealed record SentinelSettings(string SentinelId, int DegradedFailureThreshold, int DownFailureThreshold, int StableSuccessThreshold);

public sealed record SentinelRuntimeState(string State, int FailureCount, int SuccessCount, string? CorrelationId)
{
    public static SentinelRuntimeState Normal => new("normal", 0, 0, null);
    public static SentinelRuntimeState DownDetected(int failureCount) => new("down_detected", failureCount, 0, "outage-existing");
}

public sealed record HealthProbeResult(bool IsReachable, bool IsAppReady, bool IsDatabaseReady, bool IsMigrationReady, string? ErrorCode)
{
    public bool IsReady => IsReachable && IsAppReady && IsDatabaseReady && IsMigrationReady;

    public static HealthProbeResult Ready() => new(true, true, true, true, null);
    public static HealthProbeResult Unavailable(string errorCode) => new(false, false, false, false, errorCode);
}

public sealed record SentinelBinding(string TargetIdentity, string AdapterKind, string AdapterInstanceId, string? ProjectId, string? Role);

public sealed record ControlMessage(
    string Type,
    string ControlKind,
    string Reason,
    string Scope,
    string ScopeId,
    string TargetIdentity,
    string SentinelId,
    string EventId,
    DateTimeOffset IssuedAt,
    IReadOnlyList<string> Instructions,
    bool AckRequested,
    string DedupeKey);

public sealed record SentinelEvent(string EventKind, string? TargetIdentity, string PayloadJson, DateTimeOffset CreatedAt);

public sealed record SentinelTransitionResult(
    string State,
    SentinelRuntimeState RuntimeState,
    IReadOnlyList<ControlMessage> ControlMessages,
    IReadOnlyList<SentinelEvent> Events);
