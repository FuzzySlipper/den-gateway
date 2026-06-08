# Den Gateway First-Pass Follow-ups

> **Historical archive:** `den-gateway` is decommissioned. This document is retained for fact-finding or porting old behavior to active owners (Core, Channels, den-host, Hermes Bridge, FleetOps/den-network). Do not treat it as a current implementation plan or deployment runbook.

This document records cross-project contracts discovered while implementing the first standalone `den-gateway` slice. Gateway remains stubbed until these land; no item requires hidden DB coupling.

## Implemented locally

Current repo commits through `620d486f0758931278d605797933cee1deeec6cf` provide:

- ASP.NET Core skeleton and health/status endpoints.
- Gateway-owned SQLite schema and idempotent initializer.
- Typed Den Core and Den Channels client abstractions with deterministic stubs.
- Delivery/wake suppression simulation and lifecycle checks.
- Sentinel planned-maintenance / outage / recovery state-machine simulation and `GET /api/sentinel/status`.

Current local validation: `dotnet test DenGateway.slnx` passes 32 tests.

## Upstream tasks created

### den-mcp #1350 — Support den-gateway standalone service integration contract

Needed Den Core surfaces:

- stable health/readiness response with process/app/database/migration status;
- active adapter/agent binding snapshot or gateway-compatible projection;
- normalized source-summary/deep-link helper;
- durable significant-event outbox cursor;
- service-to-service auth/token support;
- Gateway sentinel reconciliation endpoint.

### den-channels #1351 — Support den-gateway channel membership and event contracts

Needed Channels surfaces:

- health/readiness probe for Gateway dependency checks;
- channel membership / wake-policy lookup;
- channel message/source lookup for routing decisions;
- channel event cursor/subscription shape;
- mirror/system-message post contract for Gateway-generated summaries.

### den-hermes-bridge #1352 — Define Hermes bridge adapter binding and delivery contract

Needed bridge behavior:

- Hermes profile/instance registration as Gateway adapter bindings;
- Gateway delivery claim/receive contract;
- wake delivery of context summary/link/source pointer;
- pause/resume `den_control` payload delivery and ack;
- delivered/acknowledged/completed/failed/expired callback contract;
- dedupe behavior for repeated wake/control events.

## Stub-mode expectations

Until those contracts land:

- `DenGateway:DenCore:UseStub=false` is the production default now that the Core gateway contract is deployed.
- `DenGateway:DenChannels:UseStub=true` remains the default.
- Den Core source summaries, event outbox, and sentinel reconciliation use the live Core gateway contract; remaining Channels/Hermes pieces still return explicit unavailable results where not implemented.
- tests pass explicit simulation payloads and fake bindings/memberships.
- Gateway must not read or write `den-mcp`, `den-channels`, or Hermes state stores directly.

## Next local Gateway work after contracts mature

- Replace stub clients with HTTP implementations behind the existing interfaces.
- Persist real delivery requests from normalized source events.
- Add adapter delivery loops or claim APIs once Hermes bridge transport is defined.
- Add sentinel reconciliation once Den Core endpoint exists.
- Expand `/api/sentinel/*` command endpoints from simulation/status to persisted operations.
