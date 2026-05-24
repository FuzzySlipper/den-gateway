# Discord Bridge Notifications (#1634)

Gateway-owned outbound Discord bridge for Channels-originated notification/wake requests. This is **infrastructure**, not an LLM ambassador workflow — it posts directly to Discord via a dedicated bot token without going through Hermes `send_message`.

## Boundary

**Owned by the bridge:**

- Outbound Discord API calls via `POST /api/discord-bridge/notifications`
- Discord bot token, target configuration, and rate guardrails under `DenGateway:DiscordBridge`
- Deduplication, cooldown, body truncation, and mention policy enforcement
- Durable request/attempt records in dedicated `discord_notifications` / `discord_notification_attempts` tables

**Not owned by the bridge:**

- Hermes profile wake/delivery loop — the bridge is a separate path
- Inbound Discord event ingestion (webhooks, interactions)
- Channel scrollback, membership, or transcript storage
- Secrets in code, logs, or error responses

## Configuration

Under `DenGateway:DiscordBridge`:

```json
{
  "DenGateway": {
    "DiscordBridge": {
      "Enabled": true,
      "BotToken": null,
      "CooldownSeconds": 30,
      "MaxBodyLength": 2000,
      "Targets": {
        "den-coder-profile": {
          "ChannelId": "1345678901234567890",
          "ThreadId": null,
          "MentionUserId": "987654321098765432",
          "WakeByMention": true
        },
        "den-reviewer-profile": {
          "ChannelId": "1345678901234567890",
          "ThreadId": "112233445566778899",
          "MentionUserId": "876543210987654321",
          "WakeByMention": true
        },
        "non-waking-target": {
          "ChannelId": "1345678901234567890",
          "ThreadId": null,
          "MentionUserId": null,
          "WakeByMention": false
        }
      }
    }
  }
}
```

Environment variable equivalent uses `__` separators:

```bash
DenGateway__DiscordBridge__Enabled=true
DenGateway__DiscordBridge__BotToken=NDIz...
DenGateway__DiscordBridge__CooldownSeconds=60
DenGateway__DiscordBridge__Targets__den-coder-profile__ChannelId=1345678901234567890
DenGateway__DiscordBridge__Targets__den-coder-profile__MentionUserId=987654321098765432
DenGateway__DiscordBridge__Targets__den-coder-profile__WakeByMention=true
```

### Configuration fields

| Field | Type | Default | Description |
|---|---|---|---|
| `Enabled` | bool | `false` | Master switch. When false, all requests return `disabled`. |
| `BotToken` | string? | `null` | Discord bot token (`NDIz...`). Required for sending. Never logged. |
| `CooldownSeconds` | int | `30` | Per-target cooldown window. Repeated notifications to the same target within this window return `cooldown`. |
| `MaxBodyLength` | int | `2000` | Maximum body length in characters. Longer bodies are truncated with `...`. |
| `Targets` | map | `{}` | Map keyed by Den agent identity (e.g. `den-coder-profile`). |

### Target fields

| Field | Type | Description |
|---|---|---|
| `ChannelId` | string | Discord channel ID to post into. Required. |
| `ThreadId` | string? | Optional thread ID within the channel. |
| `MentionUserId` | string? | Discord user ID to mention when `WakeByMention=true`. |
| `WakeByMention` | bool | When `true`, includes a targeted mention of `MentionUserId`. When `false`, all mentions are suppressed. |

## API

### `POST /api/discord-bridge/notifications`

Submit a notification request.

**Request:**

```json
{
  "target_agent_identity": "den-coder-profile",
  "body": "You have a new priority review request in project `den-gateway`.",
  "source_channel_id": "source-channel-abc",
  "source_message_id": "source-message-xyz",
  "source_project_id": "den-gateway",
  "requester": "test-runner",
  "urgency": "high",
  "dedupe_key": "discord-notify:den-gateway:source-message-xyz",
  "dry_run": false
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `target_agent_identity` | string | yes | Key into `Targets` map. Unknown values are rejected. |
| `body` | string | yes | Notification body. Truncated to `MaxBodyLength`. |
| `source_channel_id` | string | yes | Originating Den Channels channel ID. |
| `source_message_id` | string | yes | Originating Den Channels message ID. |
| `source_project_id` | string? | no | Optional project context. |
| `requester` | string | yes | Identity requesting the notification (for attribution). |
| `urgency` | string? | no | Free-text urgency qualifier. |
| `dedupe_key` | string | yes | Global deduplication key. Duplicate keys return `deduped`. |
| `dry_run` | bool? | no | When `true`, returns the rendered Discord payload without sending. |

**Response (sent):**

```json
{
  "status": "sent",
  "notification_id": 1,
  "attempt_id": 1
}
```

**Response (dry run):**

```json
{
  "status": "dry_run",
  "dry_run_payload": {
    "discord_channel_id": "1345678901234567890",
    "discord_thread_id": null,
    "content": "🔔 **test-runner** (project: den-gateway)\n\n**Urgency**: high\n\nYou have a new priority review request in project `den-gateway`.\n*Source: channel source-channel-abc, message source-message-xyz*",
    "allowed_mentions": {
      "parse": [],
      "users": ["987654321098765432"],
      "roles": []
    }
  }
}
```

**Response (rejected — unknown target):**

```json
{
  "status": "rejected",
  "error": "Unknown target agent identity: den-unknown-profile. No Discord notification sent."
}
```

**Response (deduped):**

```json
{
  "status": "deduped",
  "notification_id": 1,
  "deduped": true
}
```

**Response (cooldown):**

```json
{
  "status": "cooldown",
  "notification_id": 1,
  "error": "Target den-coder-profile is in cooldown. Skipping Discord send."
}
```

**Response (rate limited / failed):**

```json
{
  "status": "rate_limited",
  "notification_id": 1,
  "attempt_id": 1,
  "error": "Discord 429 rate limit: You are being rate limited. (code=)"
}
```

## Behavior

### Request lifecycle

1. **Validation** — required fields checked; missing fields return `validation_error`.
2. **Target resolution** — `target_agent_identity` looked up in `Targets` map; unknown targets return `rejected`.
3. **Body truncation** — body trimmed to `MaxBodyLength` chars with ellipsis if needed.
4. **Dry run** — if `dry_run=true`, returns rendered payload evidence without any side effects.
5. **Deduplication** — `dedupe_key` checked via `INSERT OR IGNORE`; duplicates return `deduped` with the existing `notification_id`.
6. **Cooldown** — per-target; if a notification was sent to the same target within `CooldownSeconds`, returns `cooldown`.
7. **Discord call** — `POST /channels/{channelId}/messages` with Bot auth, or `POST /channels/{threadId}/messages` if `threadId` set.
8. **Attempt record** — success/failure recorded in `discord_notification_attempts`.
9. **Response** — status, ids, and optional error returned.

### Mention policy

- `WakeByMention=true` + `MentionUserId` set: only that user is mentioned. `@everyone`, `@here`, and role mentions are suppressed via `allowed_mentions.parse=[]`.
- `WakeByMention=false`: no mentions at all. `allowed_mentions.users=[]`.

### Secret safety

- `BotToken` is never logged, serialized in error responses, or included in attempt payloads.
- Error messages contain Discord status messages only (e.g., "Missing Access", "You are being rate limited").
- Attempt `payload_json` records the serialized request payload without the token.

## Data model

Two SQLite tables in the gateway database:

### `discord_notifications`

| Column | Type | Description |
|---|---|---|
| `id` | INTEGER PK | Auto-increment |
| `dedupe_key` | TEXT UNIQUE | Global deduplication key |
| `target_agent_identity` | TEXT | Agent key into Targets map |
| `body` | TEXT | Truncated body |
| `body_truncated` | INT | 1 if body was truncated |
| `source_channel_id` | TEXT | Originating channel |
| `source_message_id` | TEXT | Originating message |
| `source_project_id` | TEXT? | Optional project context |
| `requester` | TEXT | Requesting identity |
| `urgency` | TEXT? | Urgency qualifier |
| `discord_channel_id` | TEXT | Resolved Discord channel |
| `discord_thread_id` | TEXT? | Resolved Discord thread |
| `mention_user_id` | TEXT? | Configured mention target |
| `wake_by_mention` | INT | Whether mention was enabled |
| `status` | TEXT | pending/sent/rate_limited/failed/cooldown |
| `created_at` | TEXT | ISO 8601 UTC |
| `updated_at` | TEXT | ISO 8601 UTC |

### `discord_notification_attempts`

| Column | Type | Description |
|---|---|---|
| `id` | INTEGER PK | Auto-increment |
| `notification_id` | INTEGER FK | References discord_notifications |
| `attempt_number` | INT | Sequential attempt number |
| `status` | TEXT | sent/rate_limited/failed/cooldown |
| `discord_message_id` | TEXT? | Discord message ID on success |
| `error_code` | TEXT? | Structured error code |
| `error_message` | TEXT? | Human-readable error |
| `payload_json` | TEXT? | Serialized Discord payload |
| `created_at` | TEXT | ISO 8601 UTC |

## Tests

Run the Discord bridge tests:

```bash
dotnet test DenGateway.slnx --filter DiscordBridge
```

Coverage includes:

- unknown target rejected
- dry_run renders payload/evidence
- duplicate dedupe key returns `deduped` with no second send
- WakeByMention=true limits mentions to target user only
- WakeByMention=false suppresses all mentions
- Discord success records notification_id and attempt_id
- Discord 429 rate limit returns `rate_limited` with no token leak
- Discord server error returns `failed` with no token leak
- body truncation at MaxBodyLength
- missing fields rejected by validation
- cooldown prevents repeated sends to same target

## Operator smoke

If no live Discord token is available:

```bash
# Dry run (no Discord call, no persistence)
curl -X POST http://127.0.0.1:5300/api/discord-bridge/notifications \
  -H 'Content-Type: application/json' \
  -d '{
    "target_agent_identity": "den-coder-profile",
    "body": "Smoke test notification body.",
    "source_channel_id": "smoke-channel",
    "source_message_id": "smoke-msg-1",
    "source_project_id": "smoke-project",
    "requester": "operator",
    "urgency": "low",
    "dedupe_key": "smoke:discord-bridge:1",
    "dry_run": true
  }'

# Unknown target rejection
curl -X POST http://127.0.0.1:5300/api/discord-bridge/notifications \
  -H 'Content-Type: application/json' \
  -d '{
    "target_agent_identity": "nonexistent-agent",
    "body": "Should be rejected.",
    "source_channel_id": "ch",
    "source_message_id": "msg",
    "requester": "operator",
    "dedupe_key": "smoke:unknown-target:1"
  }'
```

With a live token via environment:

```bash
DenGateway__DiscordBridge__Enabled=true \
DenGateway__DiscordBridge__BotToken=NDIz... \
DenGateway__DiscordBridge__Targets__den-coder-profile__ChannelId=1345678901234567890 \
DenGateway__DiscordBridge__Targets__den-coder-profile__MentionUserId=987654321098765432 \
DenGateway__DiscordBridge__Targets__den-coder-profile__WakeByMention=true \
  dotnet run --project src/DenGateway.Service
```
