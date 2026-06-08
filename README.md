# Den Gateway — historical archive

> **Decommissioned:** `den-gateway` is no longer an active deployed Den service or a target for new first-party worker routing, direct-agent delivery, wake, FleetOps, or observability work.
>
> Keep this repository only for historical fact-finding and porting old behavior into the active owning services. Do **not** restart, redeploy, expand, or add new Gateway features from this repo.

## Current active owners

| Former Gateway-shaped concern | Active owner | Current green path |
|---|---|---|
| Workflow truth, tasks, worker-pool assignments/runs/leases, reconciliation authority | `den-core` | Core worker/run/assignment APIs |
| Channel messages, memberships, subscriptions, direct-agent events, operations-hub routing, activity/current-work projections | `den-channels` | `/api/direct-agent-events`, `/api/channels/{channelId}/messages`, `/api/channels/{channelId}/activity-events`, Channels membership/subscription APIs |
| Machine-local runtime host, harness adapters, process/session/run evidence, cleanup/quarantine | `den-host` | den-host runtime evidence and worker-host APIs |
| Hermes-specific profile/config/session compatibility glue | `den-hermes-bridge` | Hermes Bridge plugin/config adapters |
| Fleet operations and machine/service administration | `den-network` / FleetOps | FleetOps/den-network service scripts and Den Web fleet surfaces |

## Repository status

This checkout preserves the old Gateway implementation and tests as an archive. The code may still compile, but that is not a signal that Gateway is live or should be revived. Historical endpoint names such as `/api/gateway/*`, `gateway_delivery`, delivery loop, adapter bindings, and sentinel status are retained only to understand or port old behavior.

If an agent lands here while working on current Den behavior, route the work away from Gateway:

- direct-agent/wake/channel delivery: `den-channels`;
- worker lifecycle and durable workflow state: `den-core` plus `den-host`;
- Hermes adapter glue: `den-hermes-bridge`;
- infrastructure/fleet operations: `den-network` / FleetOps.

## Historical contents

- `src/DenGateway.Service` — archived ASP.NET Core implementation.
- `tests/DenGateway.Service.Tests` — archived behavior/regression tests.
- `docs/` — archived specs, deployment notes, and migration-era references. Each document now carries a historical-only banner.

## Decommission policy

The canonical policy is Den document `_global/gateway-decommission-routing-policy`. In short: use Gateway only for historical fact-finding or as source material while porting behavior to the real owning service; do not make Gateway an active broker again.
