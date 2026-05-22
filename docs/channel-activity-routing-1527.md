# Gateway routing for Channels activity events (#1527)

Den Gateway accepts agent activity/breadcrumb events and forwards them to the Den Channels activity API introduced by #1526.

## Contract

Gateway endpoint:

```http
POST /api/channel-activity-events
```

Request fields mirror the Den Channels activity write contract plus the Gateway-owned immutable delivery associations:

- `channelId`
- `projectId`
- `agentIdentity`
- `deliveryRequestId`
- `displayBlockId`
- `hermesSessionKey`
- `parentHermesSessionKey`
- `parentAgentIdentity`
- `workerRunId`
- `workerRole`
- `taskId` / `threadId`
- `anchorMessageId`
- `eventType`, `status`, `sequence`
- bounded payload fields `title`, `summary`, `previewJson`, `metadataJson`
- `dedupeKey`

Gateway forwards to:

```http
POST /api/channels/{channelId}/activity-events
```

`deliveryRequestId` remains the child/producer delivery association. `displayBlockId` is a Channels-owned render-block key used to correlate child worker activity under a parent display block. Gateway accepts and forwards `displayBlockId` (serialized exactly as camelCase; snake_case consumers may refer to the same field as `display_block_id`) along with parent/worker correlation metadata, but it does **not** validate that value against Gateway `delivery_requests` and does not conflate it with `deliveryRequestId`. The legacy/non-governing name `displayDeliveryRequestId` is not part of this contract.

## Non-wake/non-terminalization invariant

Activity routing is deliberately separate from the delivery loop and delivery callback endpoints:

- It does not create `delivery_requests` rows.
- It does not poll or process `channel_messages`.
- It does not enter wake-policy evaluation.
- It does not touch final delivery dedupe handles.
- It does not call delivered/ack/fail/complete/expire callbacks.

Activity is observability. Final visible replies remain canonical completion evidence.

## Failure behavior

Activity persistence failures are **soft failures**:

- Gateway returns a `degraded` route result instead of throwing or failing the agent's final reply path.
- Failure diagnostics include the child `deliveryRequestId` plus `displayBlockId` and `workerRunId` when supplied, so parent/worker display-correlation write failures can be traced without waking or terminalizing delivery state.
- Recent write failures are visible at:

```http
GET /api/channel-activity-events/status
```

This lets operators diagnose lost breadcrumb writes without making tool-call observability part of the critical reply/terminalization path.
