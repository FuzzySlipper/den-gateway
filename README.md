# Den Gateway

Standalone .NET service for Den routing, delivery state, adapter bindings, wake suppression, and local sentinel/outage pause-resume safety.

Den Gateway is the stage manager between Den Core, Den Channels, Desktop, Hermes bridge, and future adapters. It does **not** own canonical Den workflow records or channel scrollback.

## Current slice

This repo currently contains the first .NET skeleton for Den Gateway:

- `src/DenGateway.Service` — ASP.NET Core service.
- `tests/DenGateway.Service.Tests` — endpoint smoke tests using `WebApplicationFactory`.
- `docs/den-gateway-v1-implementation-spec.md` — first-pass v1 implementation spec.
- `docs/first-pass-follow-ups.md` — cross-project contract follow-ups and stub-mode expectations.
- `docs/local-deployment.md` — local publish/run/systemd instructions.
- `/health/live` — liveness check.
- `/health/ready` — configuration/dependency-mode readiness check.
- `/api/gateway/status` — basic configured service status.
- `/api/sentinel/status` — initial sentinel status/configuration view.
- `/api/deliveries/claim` — atomic adapter claim endpoint for pending delivery requests.
- `PUT /api/adapter-bindings/heartbeat` — upserts an active adapter binding for Hermes/profile claimers.
- `/api/deliveries/{id}/delivered`, `/ack`, `/fail`, `/complete`, `/expire` — structured delivery callback endpoints with attempt/ack metadata.
- `GET /api/agent-overview/gateway-state` — read-only projection of Gateway-owned runtime slice grouped by (projectId, agentIdentity, role); shows binding freshness, delivery summary counts, and group classification.

## Configuration

Configuration lives under the `DenGateway` section.

```json
{
  "DenGateway": {
    "Database": {
      "Path": "data/den-gateway.db",
      "ApplyMigrationsOnStartup": true
    },
    "DenCore": {
      "BaseUrl": "http://192.168.1.10:18080/den-core-api",
      "UseStub": true
    },
    "DenChannels": {
      "BaseUrl": "http://192.168.1.10:18080",
      "UseStub": true
    },
    "ServiceAuth": {
      "ServiceToken": null
    },
    "Sentinel": {
      "SentinelId": "den-k8-sentinel-1",
      "PollIntervalSeconds": 10,
      "DegradedFailureThreshold": 2,
      "DownFailureThreshold": 4,
      "StableSuccessThreshold": 4,
      "BindingTtlMinutes": 120
    },
    "DeliveryLoop": {
      "Enabled": true,
      "Source": "all",
      "DiscoverProjects": true,
      "ChannelIds": ["21"],
      "ExcludedProjectIds": ["noisy-experimental-project"],
      "SeedNewProjectCursorsAtLatest": true,
      "PollIntervalSeconds": 10,
      "Limit": 100
    }
  }
}
```

Environment variable equivalents use double underscores, for example:

```bash
DenGateway__Database__Path=/var/lib/den-gateway/den-gateway.db
DenGateway__DenCore__BaseUrl=http://192.168.1.10:18080/den-core-api
DenGateway__DenCore__UseStub=false
DenGateway__DenChannels__BaseUrl=http://192.168.1.10:18080
DenGateway__DenChannels__UseStub=true
DenGateway__Sentinel__SentinelId=den-k8-sentinel-1
DenGateway__DeliveryLoop__Enabled=true
DenGateway__DeliveryLoop__Source=all
DenGateway__DeliveryLoop__DiscoverProjects=true
DenGateway__DeliveryLoop__ChannelIds__0=21
DenGateway__DeliveryLoop__SeedNewProjectCursorsAtLatest=true
```

`UseStub=true` remains useful for isolated local tests. Production now runs Den Core and Den Channels in HTTP mode via the Den Channels/Core service endpoints.

The delivery loop's intended operator path is project discovery, not manual `ProjectIds` maintenance. With `DiscoverProjects=true`, Gateway asks Den Core for normal project records, checks each project's default Den Channels lane through `/api/gateway/memberships?projectId=...`, and polls projects that have a `project_default` channel plus at least one active wake-relevant agent membership. Use `ChannelIds` for system/global lanes that intentionally have no project id, such as Agent Commons channel `21`; those channel-scoped polls use separate `channel:<id>` cursor keys and do not collide with project-scoped Channels cursors. Use `ExcludedProjectIds` only for explicit opt-outs such as archived, noisy, or experimental projects. Keep `SeedNewProjectCursorsAtLatest=true` for normal rollout so newly discovered projects start from the latest Channels event instead of replaying old channel traffic; set it false only when intentional backfill is desired.

## Build and test

```bash
dotnet restore DenGateway.slnx
dotnet build DenGateway.slnx
dotnet test DenGateway.slnx
```

## Live visible-agent smoke

After publishing/restarting the local service on `127.0.0.1:5300`, run the end-to-end synthetic smoke:

```bash
./scripts/live-visible-agent-smoke.py
```

The smoke uses only HTTP contracts and prints pass/fail evidence for:

1. Gateway readiness.
2. Den Core gateway readiness and event outbox visibility.
3. Den Channels gateway health.
4. A synthetic Core sentinel ops event with an explicit dedupe key.
5. Gateway ingestion from Core into owned delivery state.
6. Hermes-style adapter binding heartbeat, delivery claim, and completion callback.

It does not require secrets. Durable writes are limited to clearly marked synthetic Den Core ops entries plus Gateway-owned binding/delivery state. Override URLs and target identity with `GATEWAY_URL`, `DEN_CORE_URL`, `DEN_CHANNELS_URL`, `DEN_GATEWAY_SMOKE_AGENT`, and related `DEN_GATEWAY_SMOKE_*` environment variables.

## Publish locally

```bash
./scripts/publish-local.sh
```

See `docs/local-deployment.md` for manual run and systemd user-service instructions.

## Run locally

```bash
dotnet run --project src/DenGateway.Service
```

Then check:

```bash
curl http://127.0.0.1:5000/health/live
curl http://127.0.0.1:5000/health/ready
curl http://127.0.0.1:5000/api/gateway/status
curl http://127.0.0.1:5000/api/sentinel/status
```

If Kestrel chooses a different development URL, use the URL printed by `dotnet run`.

## Boundary rules

- Do not depend on being inside the `den-mcp` repo.
- Do not read/write `den-mcp` or `den-channels` SQLite databases directly.
- Consume Den Core and Den Channels through explicit HTTP/event contracts.
- When a new canonical Den state/API capability is needed, create a task in the owning `den-core` Den project instead of routing it through the historical `den-mcp` project.
- Keep Hermes-specific delivery mechanics in a thin bridge/adapter; Gateway owns routing and delivery state.

## Runbook: diagnosing sluggish direct-agent/channel delivery

### Waterfall evidence

Every delivery shown in `GET /api/agent-overview/gateway-state` now carries an optional `waterfall` block. It decomposes per-message delivery latency into Gateway-owned phases:

```
waterfall:
  statusLabel:        callback_persisted       # see labels below
  providerTiming:     provider_timing_unavailable
  gatewaySpanMs:      245.3                    # creation → claim (ms)
  bridgeSpanMs:       1240.8                   # claim → first callback (ms)
  runtimeSpanMs:      5230.1                   # first callback → terminal (ms)
  callbackPersistedSpanMs: null                # not yet computed from persisted delta
```

### Status labels

| Label | Meaning |
|---|---|
| `gateway_unclaimed` | Delivery was created but no Hermes/bridge adapter has claimed it yet. The bottleneck is either the adapter's claim polling interval or the lease claim logic. |
| `bridge_claimed_waiting_runtime` | Claimed by an adapter but no delivery/delivery callback received yet. If this persists (>>30s), the adapter may be slow processing or the runtime may have dropped the work. |
| `delivering_with_first_reply` | First callback (delivered/ack) received; waiting for terminal completion. |
| `delivered_waiting_ack_or_complete` | Bridge delivered the message to the provider; awaiting acknowledgement or tool-execution completion. |
| `acknowledged_waiting_complete` | Provider acked; awaiting final completion from the runtime. |
| `callback_persisted` | Full terminal state reached; the waterfall shows the complete timing chain. |
| `suppressed` | Not a latency issue — policy decided not to deliver. Check `suppressionReason`. |
| `terminal_unclaimed` | Terminal state (complete/fail/expire) without ever being claimed — likely a timeout expiry before any adapter picked it up. |
| `terminal_no_first_reply` | Terminal without any intermediate callback — the claim happened but the first delivery/ack callback was skipped. |

### Provider timing

**`providerTiming: "provider_timing_unavailable"`** appears on every non-suppressed waterfall where provider-level telemetry (model inference, tool execution) is absent. This is not a bug — the Gateway does not own runtime/provider instrumentation. When provider-phase timing is needed, the responsible party is the runtime/adapter (e.g. Hermes runtime, OpenCode, Claude Code), which should emit its own span data through its own telemetry channels. Do not blend bridge/runtime spans with provider inference time.

### Dominant span identification

In the smoke script output (`live-visible-agent-smoke.py`), a dominant span is printed as `>> Dominant span: <phase> (<ms> ms)`. Use this to identify the bottleneck:

- **gateway** (high `gatewaySpanMs`): the Gateway delivery loop or claim system is slow. Check poll intervals, cursor lag, or adapter heartbeat frequency.
- **bridge/runtime** (high `bridgeSpanMs`): the adapter, Hermes runtime, or provider took long to send the first callback. Check adapter logs, runtime scheduling delays, or provider cold-start.
- **runtime** (high `runtimeSpanMs`): the execution from first token to completion was long. This is the expected time for the tool/agent to produce the final result.

### Per-message table

A repeated `agent-tennis` or `direct-agent` round can produce a small table of per-message spans by querying the Gateway state endpoint after each round:

```bash
curl -s 'http://127.0.0.1:5000/api/agent-overview/gateway-state?projectId=den-gateway&agentIdentity=<target>&includeTerminalMinutes=480' \
  | jq '.agents[].recentDeliveries[] | {id: .deliveryRequestId, status: .status, label: .waterfall.statusLabel, gateway: .waterfall.gatewaySpanMs, bridge: .waterfall.bridgeSpanMs, runtime: .waterfall.runtimeSpanMs}'
```

Expected output for a healthy exchange:

```
{"id": 101, "status": "completed", "label": "callback_persisted", "gateway": 12.5, "bridge": 340.2, "runtime": 5230.1}
{"id": 102, "status": "completed", "label": "callback_persisted", "gateway": 8.3, "bridge": 280.7, "runtime": 4120.9}
```

Large `gateway` spans on fresh deliveries suggest cold-start cursor seeding or loop delay. Large `bridge` spans suggest runtime warmup or provider cold-start. Large `runtime` spans are the actual tool response time. `suppressed` entries indicate policy filtering, not latency.
