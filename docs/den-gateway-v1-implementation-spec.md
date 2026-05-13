# Den Gateway v1 Implementation Spec

Status: first-pass implementation spec for Den task #1342.

## Boundary

`den-gateway` is a standalone .NET service that owns routing, delivery state, adapter bindings, wake suppression, and local outage/sentinel safety. It does not own canonical Den workflow data or channel scrollback.

Owned by `den-gateway` v1:

- local SQLite persistence for gateway adapter bindings, delivery requests, delivery attempts, sentinel state, binding snapshots, sentinel events, and maintenance windows;
- HTTP API for health/readiness, adapter binding heartbeat/listing, delivery request simulation, delivery status transitions, sentinel status, and operator pause/resume/maintenance commands;
- conservative suppression policy simulation for wake/delivery decisions;
- client abstractions for Den Core and Den Channels using explicit HTTP/event contracts, with stub implementations for local tests;
- fake/local adapters for deterministic delivery lifecycle tests.

Not owned by `den-gateway` v1:

- Den tasks, reviews, docs, workers, messages, identities, or source-of-truth project metadata;
- channel room/message/membership/reaction storage;
- Desktop UI;
- Hermes runtime implementation;
- direct reads/writes of `den-mcp` or `den-channels` SQLite databases.

## Repo and service shape

Initial layout:

```text
DenGateway.slnx
Directory.Build.props
README.md
docs/den-gateway-v1-implementation-spec.md
src/DenGateway.Service
  Program.cs
  appsettings.json
  Options/
  Persistence/
  Contracts/
  Clients/
  Bindings/
  Deliveries/
  Suppression/
  Sentinel/
  Adapters/
tests/DenGateway.Service.Tests
```

The service should use ASP.NET Core minimal APIs for the first pass and xUnit plus `Microsoft.AspNetCore.Mvc.Testing` for endpoint/state-machine tests.

Configuration root: `DenGateway`.

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
    }
  }
}
```

Environment variables use `__`, for example `DenGateway__Database__Path=/var/lib/den-gateway/den-gateway.db`.

## Local SQLite persistence

Use gateway-owned SQLite only. Do not attach other service DBs.

### `gateway_adapter_bindings`

Routes Den targets to runtime/platform delivery endpoints.

Fields:

- `id` integer primary key;
- `adapter_kind` text: `hermes_gateway`, `den_desktop`, `local_sentinel`, `discord`, `telegram`, `test`;
- `adapter_instance_id` text;
- `agent_identity` text nullable;
- `user_identity` text nullable;
- `project_id` text nullable;
- `role` text nullable;
- `status` text: `active`, `degraded`, `inactive`;
- `capabilities_json` text not null default `{}`;
- `metadata_json` text not null default `{}`;
- `last_seen_at` text UTC nullable;
- `expires_at` text UTC nullable;
- `created_at`, `updated_at` text UTC;
- unique index on `(adapter_kind, adapter_instance_id, coalesce(project_id,''), coalesce(agent_identity,''), coalesce(user_identity,''), coalesce(role,''))`.

### `delivery_requests`

Authoritative gateway delivery/wake/pause/resume state.

Fields:

- `id` integer primary key;
- `source_kind` text: `channel_message`, `notification`, `task_message`, `worker_event`, `manual_admin`, `sentinel_control`, `external_adapter_message`;
- `source_id` text nullable;
- `source_project_id` text nullable;
- `target_type` text: `agent`, `role`, `instance`, `adapter`, `user`;
- `target_identity` text;
- `project_id`, `task_id`, `channel_id` nullable;
- `delivery_mode` text: `record_only`, `notify`, `wake`, `pause`, `resume`;
- `priority` integer default 3;
- `reason` text nullable;
- `context_summary` text nullable;
- `context_link` text nullable;
- `metadata_json` text not null default `{}` for bounded source/delivery metadata copied into DTOs;
- `status` text: `pending`, `suppressed`, `delivering`, `delivered`, `acknowledged`, `completed`, `failed`, `expired`;
- `suppression_reason` text nullable;
- `dedupe_key` text not null unique;
- `cascade_depth` integer default 0;
- `attempt_count` integer default 0;
- `lease_expires_at`, `next_attempt_at`, `expires_at`, `created_at`, `updated_at` text UTC.

### `delivery_attempts`

Append-only attempt/audit rows.

Fields: `id`, `delivery_request_id`, `adapter_binding_id`, `attempt_number`, `status`, `error_code`, `error_message`, `ack_kind`, `external_message_id`, `session_id`, `observed_at`, `payload_json`, `created_at`.

### `sentinel_state`

Single-row current state.

Fields: `id=1`, `state`, `reason`, `last_den_health_json`, `last_den_healthy_at`, `failure_count`, `success_count`, `current_maintenance_id`, `updated_at`.

Valid states: `normal`, `planned_pause_pending`, `pausing`, `paused_den_maintenance`, `degraded`, `down_detected`, `waiting_for_stable`, `resume_pending`, `normal_after_resume`.

### `binding_snapshots`

Last-known active targets for outage contact.

Fields: `snapshot_id`, `captured_at`, `source_den_generation`, `agent_identity`, `project_id`, `role`, `adapter_kind`, `adapter_instance_id`, `transport_endpoint`, `status`, `last_seen_at`, `expires_at`, `metadata_json`.

### `sentinel_events`

Local spool for outage audit/reconciliation.

Fields: `id`, `event_kind`, `target_identity`, `delivery_request_id`, `payload_json`, `created_at`, `reconciled_at`.

Kinds: `health_degraded`, `down_detected`, `pause_sent`, `pause_ack`, `resume_sent`, `resume_ack`, `delivery_failed`, `maintenance_notice_received`, `reconciled`.

### `maintenance_windows`

Fields: `maintenance_id`, `reason`, `requested_by`, `not_before`, `expected_until`, `state`, `nonce`, `auth_metadata_json`, `created_at`, `updated_at`.

## V1 HTTP API

Health/config:

- `GET /health/live` -> `{ "status": "live" }`.
- `GET /health/ready` -> checks config, DB migration/access, and stub/client mode; returns service readiness.
- `GET /api/gateway/status` -> service status, DB path, client mode, counts by delivery/sentinel state.

Adapter bindings:

- `PUT /api/adapter-bindings/heartbeat` upserts a binding heartbeat.
- `GET /api/adapter-bindings?projectId=&agentIdentity=&role=&status=` lists current bindings.
- `GET /api/adapter-bindings/{id}` returns one binding.

Deliveries and simulation:

- `POST /api/deliveries/simulate` evaluates target resolution/suppression without committing a delivery.
- `POST /api/deliveries` creates a delivery request or records a suppressed request with reason.
- `GET /api/deliveries?status=&targetIdentity=&projectId=&afterId=&limit=` lists delivery requests.
- `GET /api/deliveries/{id}` returns request plus attempts.
- `POST /api/deliveries/claim` atomically selects eligible pending deliveries for an adapter binding, transitions them to `delivering`, appends attempt rows, and leases them for a bounded interval. Claim filters: `adapter_kind`, `adapter_instance_id`, `project_id`, `agent_identity`, `role`, `accepted_delivery_modes`, `limit`, and `lease_seconds`.
- `POST /api/deliveries/{id}/mark-delivering` records claim/attempt start.
- `POST /api/deliveries/{id}/delivered` marks delivered and accepts structured callback metadata: `attempt_id`, `ack_kind`, `adapter_kind`, `adapter_instance_id`, `external_message_id`, `session_id`, `observed_at`, and bounded `metadata_json`.
- `POST /api/deliveries/{id}/ack` marks acknowledged with the same callback metadata.
- `POST /api/deliveries/{id}/fail` records failed attempt and retry/failed status with the same callback metadata plus optional `error_code`/`error_message`.
- `POST /api/deliveries/{id}/complete` marks completed with idempotent terminal callback handling for duplicate bridge retries where practical.
- `POST /api/deliveries/{id}/expire` marks expired with idempotent terminal callback handling for duplicate bridge retries where practical.

Sentinel/operator:

- `GET /api/sentinel/status` returns state, health counters, current maintenance, unreconciled event counts.
- `POST /api/sentinel/health/check` runs one Den health poll and advances state deterministically.
- `POST /api/sentinel/maintenance/start` creates planned maintenance and pause deliveries for target scope.
- `POST /api/sentinel/maintenance/complete` moves to recovery/resume when Den is stable.
- `POST /api/sentinel/pause` manually creates pause deliveries.
- `POST /api/sentinel/resume` manually creates resume deliveries.
- `GET /api/sentinel/events?since=&limit=` lists local sentinel events.

## V1 CLI command shape

The first pass can expose these as README-documented HTTP calls or a small .NET console later. The command contract is:

- `den-gateway status`
- `den-gateway sentinel status`
- `den-gateway maintenance start --reason ... [--expected-until ...]`
- `den-gateway maintenance complete`
- `den-gateway pause --scope all-active|project|agent|role --scope-id ... --reason ...`
- `den-gateway resume --scope all-active|project|agent|role --scope-id ... --reason ...`
- `den-gateway active-agents`
- `den-gateway events --since ...`

## Suppression policy v1

Suppression reasons should be stored as stable strings:

- `self_message`
- `pure_reaction`
- `mirror_summary_suppressed`
- `duplicate_dedupe_key`
- `target_cooldown`
- `auto_reply_window_exceeded`
- `cascade_depth_exceeded`
- `agent_tennis_requires_human_reset`
- `den_paused`
- `den_unavailable`
- `ambiguous_target`
- `no_active_binding`
- `expired_source`
- `unsafe_delivery_mode`
- `unsupported_policy`

Minimum simulation scenarios:

1. self-message suppression;
2. reaction suppression;
3. mirror summary suppression;
4. direct mention produces one wake;
5. duplicate dedupe key produces one delivery;
6. cooldown suppresses repeated human pings;
7. agent tennis suppresses further auto-wakes until a human message;
8. cascade depth limit;
9. outage pause suppresses normal wake but allows `pause`/`resume` modes;
10. ambiguous role target records suppression instead of guessing;
11. no active binding records suppression/failure;
12. command result does not wake unless explicitly mentioned.

## Den Core client contract/stub

Gateway client abstraction should start with:

- `GetHealthAsync()` for liveness/readiness and DB/migration status;
- `ListActiveBindingsAsync()` for compatibility with current `agent_instance_bindings`/future gateway projection;
- `GetSourceSummaryAsync(sourceKind, sourceId, projectId)` for display summaries/deep links;
- `ReadEventOutboxAsync(after, projectId, limit)` for significant Den events;
- `PostGatewayReconciliationEventsAsync(events)` for sentinel delivery/outage reconciliation.

Until Den Core support lands, tests use a stub client. Runtime config may use stub mode for local development.

## Den Channels client contract/stub

Gateway client abstraction should start with:

- `GetChannelMessageAsync(channelMessageId)` or source payload lookup for simulations;
- `ListMembershipsAsync(channelId)` for wake-policy inputs;
- `PostMirrorOrSystemMessageAsync(...)` for selected delivery/sentinel summaries, if enabled;
- `ReadChannelEventsAsync(after, projectId, limit)` later, once channels event/feed contract exists.

Until channel wake contracts mature, v1 tests pass explicit simulation payloads and membership facts.

## Pause/resume control payloads

Use the ADR payload shape with:

- `type: den_control`;
- `control_kind: pause|resume`;
- `reason`, `scope`, `scope_id`, `sentinel_id`, `event_id`, `issued_at`;
- pause includes `den_last_seen_healthy_at` and `expected_resume_after`;
- resume includes `den_stable_since`;
- `instructions` list;
- `ack_requested` boolean;
- stable `dedupe_key` (`den-pause:<maintenance-or-outage-id>:<target>` or `den-resume:<maintenance-or-outage-id>:<target>`).

## Deferred until contracts mature

- Real Hermes profile wake/pause/resume transport and ack callbacks;
- real channel membership wake-policy ingestion beyond explicit/stub test payloads;
- live event subscriptions/SSE/WebSockets;
- Desktop channel-first UI;
- external Discord/Telegram adapters;
- dispatch migration/deletion tooling;
- service-token enforcement beyond config placeholder and request header support.

## Required upstream Den Core follow-up

Create/track a `den-mcp` task for Gateway-facing support:

- harden `/health/ready` response with process/app/database/migration status;
- expose active adapter/agent binding snapshot or gateway-compatible projection;
- expose source-summary/deep-link helper usable by Gateway and Channels;
- expose durable significant-event outbox cursor;
- define service-to-service auth/token enforcement;
- add reconciliation endpoint for sentinel pause/resume/outage events.

Gateway must proceed with stubs until those contracts are available.

## Acceptance tests for the first pass

Skeleton (#1343):

- solution restores/builds/tests;
- `/health/live`, `/health/ready`, `/api/gateway/status` smoke tests pass;
- README documents build/run/config.

Persistence (#1344):

- fresh DB migration creates all gateway tables;
- migrations are idempotent;
- dedupe constraints prevent duplicate delivery requests;
- binding heartbeat upsert updates `last_seen_at` and status.

Client stubs (#1345):

- Den Core/Channels stub modes return deterministic health/project/source/membership fixtures;
- unavailable real clients produce typed unavailable results, not crashes.

Delivery/suppression (#1346):

- all minimum simulation scenarios above pass;
- delivery attempt/ack/fail transitions are validated.

Sentinel (#1347):

- planned maintenance creates pause deliveries and events;
- unplanned health failure threshold moves to `down_detected` and creates pause deliveries;
- stable recovery threshold creates resume deliveries;
- normal wake deliveries are suppressed during paused/down states;
- local events remain listable for later reconciliation.
