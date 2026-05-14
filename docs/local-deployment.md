# Den Gateway local deployment

This repo is now shaped as a deployable local ASP.NET Core service.

## Build/publish

```bash
./scripts/publish-local.sh
```

The publish output defaults to:

```text
/home/dev/den-gateway/artifacts/publish/DenGateway.Service
```

## Run manually

```bash
ASPNETCORE_ENVIRONMENT=Production \
  /home/dev/den-gateway/artifacts/publish/DenGateway.Service/DenGateway.Service \
  --urls http://127.0.0.1:5300
```

Smoke check:

```bash
curl http://127.0.0.1:5300/health/live
curl http://127.0.0.1:5300/health/ready
curl http://127.0.0.1:5300/api/gateway/status
curl http://127.0.0.1:5300/api/sentinel/status
```

## Systemd user service candidate

A candidate unit is provided at:

```text
deploy/den-gateway.service
```

Install as the account that should own the service:

```bash
mkdir -p ~/.config/systemd/user
cp /home/dev/den-gateway/deploy/den-gateway.service ~/.config/systemd/user/den-gateway.service
systemctl --user daemon-reload
systemctl --user enable --now den-gateway.service
systemctl --user status den-gateway.service
```

This intentionally uses a loopback listener on `127.0.0.1:5300`.

## Live visible-agent smoke

After the service is published and restarted, run:

```bash
/home/dev/den-gateway/scripts/live-visible-agent-smoke.py
```

The smoke posts a marked synthetic sentinel ops event into Den Core, verifies it appears in the Core outbox, polls Gateway ingestion, performs a Hermes-style claim, and completes the delivery with a structured callback. It uses only HTTP contracts and does not require secrets.

## Production defaults

`src/DenGateway.Service/appsettings.Production.json` defaults:

- database: `/home/dev/den-gateway/data/den-gateway.db`
- Den Core: `http://192.168.1.10:18080/den-core-api`, `UseStub=false` now that den-core gateway contract is deployed
- Den Channels: `http://192.168.1.10:18080`, `UseStub=false` now that den-channels #1351 is complete

## Current external dependency state

- Den Channels gateway contracts are consumed through `HttpDenChannelsClient` when `DenGateway:DenChannels:UseStub=false`.
- Den Core gateway contracts are consumed through `HttpDenCoreClient` when `DenGateway:DenCore:UseStub=false`.
- Hermes profile delivery remains outside this service until den-hermes-bridge #1352 lands.
