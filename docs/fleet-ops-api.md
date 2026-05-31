# Fleet Operations API

The Den Gateway FleetOps API provides a typed, auditable interface for restarting Hermes gateway services and running approved fleet maintenance scripts. No arbitrary shell, path, or command arguments are accepted — only allowlisted actions from the registry.

## Security Model

- **No arbitrary shell**: all actions are typed entries in a declarative allowlist. No free-form command/path/args.
- **No broad sudo**: commands run with the Gateway process identity. For `systemctl --user`, the gateway surface failure diagnostics if the user manager is unreachable (a separate sentinel/helper is a follow-up).
- **Secret redaction**: all command output is scanned for token/API key/password/.env-like patterns and redacted to `[REDACTED]` before returning.
- **Output truncation**: output is capped at `MaxOutputLines` (default: 100).
- **Confirmation enforcement**: mutating high-risk actions require an explicit `confirmation` field in the request.
- **Dry-run semantics**: mutating actions with `dryRun=true` resolve to safe preview/status commands when available.

## Endpoints

### `GET /api/gateway/fleet-ops`

Returns the current fleet operations overview: discovered Hermes gateway service units, available allowlisted actions, and recent run history.

**Response shape:**

```json
{
  "service": "den-gateway",
  "generatedAt": "2026-05-31T06:30:00Z",
  "serviceUnits": [
    {
      "unitName": "hermes-gateway@spawned-coder.service",
      "profileName": "spawned-coder",
      "activeState": "active",
      "subState": "running",
      "description": "spawned-coder (active/running)"
    },
    {
      "unitName": "hermes-gateway@runner.service",
      "profileName": "runner",
      "activeState": "active",
      "subState": "running"
    }
  ],
  "actions": [
    {
      "actionId": "fleet-status",
      "label": "Fleet Status",
      "riskLevel": "low",
      "mutating": false,
      "supportsDryRun": true,
      "needsConfirmation": false,
      "timeoutSeconds": 60
    },
    {
      "actionId": "restart-all",
      "label": "Restart All Gateway Services",
      "riskLevel": "high",
      "mutating": true,
      "supportsDryRun": true,
      "needsConfirmation": true,
      "confirmationCopy": "This will restart ALL Hermes gateway services. Active sessions may be interrupted.",
      "timeoutSeconds": 120
    },
    {
      "actionId": "fleet-update",
      "label": "Update Hermes Fleet",
      "riskLevel": "high",
      "mutating": true,
      "disabledReason": "Requires explicit --restart-profiles; implement in follow-up task"
    }
  ],
  "discoveryDiagnostics": null,
  "recentRuns": [...]
}
```

**Fields:**
- `serviceUnits` — discovered `hermes-gateway@*.service` units from `systemctl list-units`; empty if discovery unavailable (with `discoveryDiagnostics`)
- `actions` — all allowlisted action descriptors; disabled actions have a `disabledReason`
- `recentRuns` — last 10 run records (if any)

### `POST /api/gateway/fleet-ops/actions/{actionId}/runs`

Execute a typed, allowlisted fleet action.

**Request:**

```json
{
  "actionId": "restart-all",
  "dryRun": false,
  "args": {},
  "confirmation": "yes"
}
```

**Fields:**
- `actionId` (required) — the action to run
- `dryRun` (optional, default: false) — if true, non-mutating actions execute normally; mutating actions resolve to a safe preview/status command
- `args` (optional) — typed arguments per the action's argSchema (e.g., `{"profile": "spawned-coder"}` for `restart-profile`)
- `confirmation` (optional) — required for actions with `needsConfirmation: true`; value must be "yes", "true", "confirm", "confirmed", or match the action's `confirmationCopy`

**Response (200 OK):**

```json
{
  "runId": "a1b2c3d4e5f678901234567890abcdef",
  "actionId": "restart-all",
  "args": {},
  "status": "completed",
  "createdAt": "2026-05-31T06:30:00Z",
  "startedAt": "2026-05-31T06:30:00Z",
  "finishedAt": "2026-05-31T06:30:02Z",
  "exitCode": 0,
  "stdoutTail": [
    "Restarting hermes-gateway@spawned-coder.service...",
    "Restarting hermes-gateway@runner.service..."
  ],
  "stderrTail": [],
  "wasDryRun": false
}
```

**Response (400 Bad Request — error):**

```json
{
  "runId": "b2c3d4e5f678901234567890abcdef01",
  "actionId": "nonexistent",
  "args": {},
  "status": "failed",
  "createdAt": "2026-05-31T06:30:00Z",
  "finishedAt": "2026-05-31T06:30:00Z",
  "errorMessage": "Unknown action: nonexistent"
}
```

### `GET /api/gateway/fleet-ops/runs/{runId}`

Retrieve a specific action run by its run ID.

**Response (200 OK):**

```json
{
  "run": {
    "runId": "a1b2c3d4e5f678901234567890abcdef",
    "actionId": "restart-all",
    "status": "completed",
    "exitCode": 0,
    ...
  }
}
```

**Response (404 Not Found):**

```json
{ "run": null }
```

## Action Catalog

| Action ID | Label | Mutating | Risk | Confirmation | Dry-Run |
|-----------|-------|----------|------|-------------|---------|
| `fleet-status` | Fleet Status | No | low | No | Yes (executes safely) |
| `fleet-smoke` | Fleet Smoke Checks | No | low | No | Yes (executes safely) |
| `restart-all` | Restart All Gateway Services | Yes | high | **Required** | Yes (preview) |
| `restart-failed` | Restart Failed Services Only | Yes | medium | No | Yes (preview) |
| `restart-profile` | Restart Profile Service | Yes | medium | No | Yes (status check) |
| `fleet-update` | Update Hermes Fleet | **Disabled** | — | — | — |
| `deploy-skills` | Deploy Shared Skills | **Disabled** | — | — | — |
| `propagate-auth` | Propagate Auth Credentials | **Disabled** | — | — | — |
| `archive-launchers` | Archive Stale Launchers | **Disabled** | — | — | — |

## Action Details

### fleet-status
- **Script:** `restart-agent-services` (no `--yes`)
- **Args:** none
- **Behavior:** returns current status of Hermes gateway services; non-mutating

### fleet-smoke
- **Script:** `smoke-hermes-fleet.sh`
- **Args:** none
- **Behavior:** runs non-mutating fleet smoke checks

### restart-all
- **Script:** `restart-agent-services --yes`
- **Args:** none
- **Confirmation required:** yes ("yes", "true", "confirm", or exact confirmation copy)
- **Dry-run:** runs `restart-agent-services` without `--yes` (preview/status)

### restart-failed
- **Script:** `restart-agent-services --yes --failed-only`
- **Args:** none
- **Dry-run:** runs `restart-agent-services` without `--yes --failed-only` (preview)

### restart-profile
- **Command:** `systemctl --user restart hermes-gateway@{profile}.service`
- **Args:** `profile` (string, pattern `^[a-zA-Z0-9_-]+$`)
- **Profile validation:** profile must match pattern AND be a discovered current service unit
- **Dry-run:** `systemctl --user is-active hermes-gateway@{profile}.service`

## Mapping to Fleet Scripts

The actions correspond to scripts under `/home/agents/local/hermes-fleet/bin/`:

| Script | Used By |
|--------|---------|
| `restart-agent-services` | `fleet-status`, `restart-all`, `restart-failed` |
| `smoke-hermes-fleet.sh` | `fleet-smoke` |
| `systemctl` (system) | `restart-profile` |

## Configuration

Configured in `appsettings.json` under the `FleetOps` section:

```json
{
  "FleetOps": {
    "ScriptsDirectory": "/home/agents/local/hermes-fleet/bin",
    "SystemctlPath": "systemctl",
    "MaxOutputLines": 100,
    "MaxRuns": 1000,
    "DefaultTimeoutSeconds": 60,
    "StatusTimeoutSeconds": 30,
    "RestartTimeoutSeconds": 120
  }
}
```

## Error Handling

- Unknown action ID → 400 with `errorMessage`
- Disabled action → 400 with `errorMessage` including `disabledReason`
- Missing required confirmation → 400
- Invalid args → 400 with description of validation failure
- Profile not in discovered units → 400 with diagnostic
- Discovery unavailable for `restart-profile` → 400
- Execution failure → 400 with `exitCode`, `stderrTail`, `errorMessage`

## Implementation Notes

- All execution uses `System.Diagnostics.Process` with captured stdout/stderr
- Secret redaction is applied to all output before storage/response
- Runs are stored in an in-memory bounded store (max 1000 entries, LRU eviction)
- Each run is logged via `ILogger` for audit trail
- The action registry is declarative — only `FleetOpsActionRegistry.cs` defines what can be executed
