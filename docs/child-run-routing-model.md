# Child-run routing model for shared-profile worker pools

> **Historical archive:** `den-gateway` is decommissioned. This document is retained for fact-finding or porting old behavior to active owners (Core, Channels, den-host, Hermes Bridge, FleetOps/den-network). Do not treat it as a current implementation plan or deployment runbook.

## Problem

Shared-profile worker pools (e.g., `spawned-coder` with capacity=4) need each active child
run to be Gateway-visible and routable. Without per-child-run visibility, Gateway treats
the profile as a single monolith, making delivery routing ambiguous and operator dashboards
opaque.

## Target model: child-bound temporary bindings

Each child run registers a temporary Gateway adapter binding at startup:

```
adapter_kind:       hermes_profile
adapter_instance_id: hermes:{host}:{profile}:{run_id}
                     e.g. hermes:den-k8:spawned-coder:piw_20260531122347_3f75b910
agent_identity:     pool-coder-NN  (or slot identity)
project_id:         den-core (or applicable project)
role:               coder
status:             active
```

When the child run completes, stalls, or crashes, the binding is deregistered or allowed to
expire via TTL. Stale bindings are reconciled by Gateway-local TTL checks.

## Interim routing model (current)

Until Bridge/Channels work (`den-hermes-bridge` tasks tracked separately) enables:

1. Per-child adapter binding registration on Core at child start
2. Per-child Channels membership for direct-agent message routing

The interim model operates as follows:

- **Gateway delivers to the supervisor profile binding** (target_type=`agent`,
  target_identity=`pool-coder-NN`).
- **The Bridge supervisor reads child-run metadata from the claimed delivery**
  (assignment_id, run_id, agent_instance_id, pool_member_id) and dispatches the
  delivery payload to the correct child Hermes session.
- **Gateway preserves child-run correlation metadata through the full delivery
  lifecycle:** create → claim → deliver → callback (ack, fail, complete). Every
  callback carries assignment_id, run_id, and session_id so the Bridge can
  correlate provider responses to the correct child worker run.

## Correlation fields flowing through delivery lifecycle

| Stage | Fields carried |
|-------|--------------|
| **Create** (Core outbox → Gateway delivery) | assignment_id, worker_identity, worker_role, agent_instance_id, pool_member_id, run_id (in metadata) |
| **Claim** (Gateway → Bridge supervisor) | All of the above, plus lease_expires_at |
| **Deliver** (Bridge supervisor → child Hermes) | assignment_id, run_id, session_id |
| **Callback** (child Hermes → Gateway) | attempt_id, ack_kind, session_id, assignment_id (in metadata) |
| **Complete/Fail** (Gateway → Core reconciliation) | delivery_request_id, assignment_id, run_id |

## Gateway-side child-run visibility

The Gateway state overview (`/api/gateway/state`) now shows per-agent child-run status:

```json
{
  "agents": [{
    "agentKey": "den-core:pool-coder-01:coder",
    "profileIdentity": "spawned-coder",
    "childrenCount": 2,
    "childRuns": [
      {
        "adapterInstanceId": "hermes:den-k8:spawned-coder:piw_abc",
        "status": "busy",
        "assignmentId": "98765",
        "runId": "piw_abc"
      },
      {
        "adapterInstanceId": "hermes:den-k8:spawned-coder:piw_def",
        "status": "stale",
        "flags": ["stale"]
      }
    ],
    "flags": ["has_multiple_children"]
  }]
}
```

Child-run status is derived from binding freshness + delivery state:

- **available**: active binding, no non-terminal deliveries
- **busy**: active binding, has pending/delivering/delivered deliveries
- **stale**: inactive binding, TTL expired
- **crashed**: inactive binding but non-terminal deliveries still exist (capacity leak)
- **released**: inactive binding, no active deliveries

## Stale assignment reconciliation

When a delivery has an `assignment_id` and remains in `delivering`/`delivered` state
beyond the `StaleAssignmentMinutes` threshold (15 min), Gateway flags it as
`stale_assignment` / `stuck` in status projections. Gateway does not silently mutate
Core assignment state or release Core capacity; terminal assignment release remains owned by Core/orchestration. The flag gives operators and follow-up automation enough evidence to reconcile stuck work without hiding the lifecycle transition.

## Deferred work (separate tasks)

The following capabilities require Changes/Bridge work and are deferred to follow-up
tasks:

1. **Automatic per-child adapter binding registration** — Bridge registers child bindings
   on Core at child process start
2. **Per-child Channels membership** — each child gets a Den Channels identity for
   direct-agent message routing
3. **Core `agent_instance_bindings` population per child run** — currently bindings are
   profile-level, need per-child entries
