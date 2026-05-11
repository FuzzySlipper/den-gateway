using DenGateway.Service.Sentinel;

namespace DenGateway.Service.Tests;

public class SentinelStateMachineTests
{
    [Fact]
    public void StartMaintenanceCreatesPauseControlMessagesForActiveBindings()
    {
        var machine = new SentinelStateMachine(new SentinelSettings(
            SentinelId: "den-k8-sentinel-1",
            DegradedFailureThreshold: 2,
            DownFailureThreshold: 4,
            StableSuccessThreshold: 3));
        var bindings = new[] { Binding("agent-a"), Binding("agent-b") };

        var result = machine.StartMaintenance("kernel upgrade", "patch", bindings, Now);

        Assert.Equal("paused_den_maintenance", result.State);
        Assert.Equal(2, result.ControlMessages.Count);
        Assert.All(result.ControlMessages, message =>
        {
            Assert.Equal("pause", message.ControlKind);
            Assert.Contains("Do not start new Den-dependent work.", message.Instructions);
            Assert.Contains("Do not guess from stale Den state.", message.Instructions);
            Assert.StartsWith("den-pause:", message.DedupeKey);
        });
        Assert.Contains(result.Events, e => e.EventKind == "maintenance_notice_received");
        Assert.Equal(2, result.Events.Count(e => e.EventKind == "pause_sent"));
    }

    [Fact]
    public void ConsecutiveHealthFailuresMoveNormalToDegradedThenDownAndPauseActiveBindings()
    {
        var machine = new SentinelStateMachine(new SentinelSettings(
            SentinelId: "den-k8-sentinel-1",
            DegradedFailureThreshold: 2,
            DownFailureThreshold: 3,
            StableSuccessThreshold: 3));
        var state = SentinelRuntimeState.Normal;
        var bindings = new[] { Binding("agent-a") };

        state = machine.ApplyHealthProbe(state, HealthProbeResult.Unavailable("tcp_failed"), bindings, Now).RuntimeState;
        var degraded = machine.ApplyHealthProbe(state, HealthProbeResult.Unavailable("tcp_failed"), bindings, Now);
        var down = machine.ApplyHealthProbe(degraded.RuntimeState, HealthProbeResult.Unavailable("tcp_failed"), bindings, Now);

        Assert.Equal("degraded", degraded.State);
        Assert.Empty(degraded.ControlMessages);
        Assert.Equal("down_detected", down.State);
        var pause = Assert.Single(down.ControlMessages);
        Assert.Equal("pause", pause.ControlKind);
        Assert.Equal("den_unreachable", pause.Reason);
    }

    [Fact]
    public void StableHealthWindowCreatesResumeControlMessageWithRefreshInstructions()
    {
        var machine = new SentinelStateMachine(new SentinelSettings(
            SentinelId: "den-k8-sentinel-1",
            DegradedFailureThreshold: 2,
            DownFailureThreshold: 3,
            StableSuccessThreshold: 2));
        var bindings = new[] { Binding("agent-a") };
        var state = SentinelRuntimeState.DownDetected(failureCount: 3);

        var waiting = machine.ApplyHealthProbe(state, HealthProbeResult.Ready(), bindings, Now);
        var resumed = machine.ApplyHealthProbe(waiting.RuntimeState, HealthProbeResult.Ready(), bindings, Now.AddSeconds(30));

        Assert.Equal("waiting_for_stable", waiting.State);
        Assert.Empty(waiting.ControlMessages);
        Assert.Equal("normal_after_resume", resumed.State);
        var resume = Assert.Single(resumed.ControlMessages);
        Assert.Equal("resume", resume.ControlKind);
        Assert.Contains("Refresh Den state before continuing.", resume.Instructions);
        Assert.Contains("Resume only work still valid in Den.", resume.Instructions);
        Assert.StartsWith("den-resume:", resume.DedupeKey);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-11T12:00:00Z");

    private static SentinelBinding Binding(string identity) => new(
        TargetIdentity: identity,
        AdapterKind: "test",
        AdapterInstanceId: $"{identity}-adapter",
        ProjectId: "den-gateway",
        Role: "runner");
}
