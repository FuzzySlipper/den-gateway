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
- `/api/deliveries/{id}/delivered`, `/ack`, `/fail`, `/complete`, `/expire` — structured delivery callback endpoints with attempt/ack metadata.

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
```

`UseStub=true` remains useful for isolated local tests. Production now runs Den Core and Den Channels in HTTP mode via the Den Channels/Core service endpoints.

## Build and test

```bash
dotnet restore DenGateway.slnx
dotnet build DenGateway.slnx
dotnet test DenGateway.slnx
```

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
- When a new Den Core capability is needed, create a task in the `den-mcp` Den project instead of editing that repo from here.
- Keep Hermes-specific delivery mechanics in a thin bridge/adapter; Gateway owns routing and delivery state.
