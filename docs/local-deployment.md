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

## Production defaults

`src/DenGateway.Service/appsettings.Production.json` defaults:

- database: `/home/dev/den-gateway/data/den-gateway.db`
- Den Core: `http://127.0.0.1:5199`, still `UseStub=true` until den-mcp #1350 lands
- Den Channels: `http://127.0.0.1:5299`, `UseStub=false` now that den-channels #1351 is complete

## Current external dependency state

- Den Channels gateway contracts are consumed through `HttpDenChannelsClient` when `DenGateway:DenChannels:UseStub=false`.
- Den Core integration remains stubbed until den-mcp #1350 is implemented/deployed.
- Hermes profile delivery remains outside this service until den-hermes-bridge #1352 lands.
