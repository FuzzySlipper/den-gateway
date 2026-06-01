# Den Gateway live FleetOps deployment on den-srv

Task #1810 deploys the Gateway FleetOps API behind Den Web without colliding with the existing Den Channels `/api/gateway/*` namespace.

## Live service layout

- Service: `den-gateway.service`
- Host: `den-srv`
- Unit template: `deploy/den-gateway.den-srv.service`
- Process user: `agent:agents`
- Listen URL: `http://127.0.0.1:5300`
- Publish root: `/data/services/den-gateway/publish`
- Data root: `/data/services/den-gateway/data`
- Database: `/data/services/den-gateway/data/den-gateway.db`

The live unit intentionally sets `DenGateway__DeliveryLoop__Enabled=false` for the FleetOps rollout. This exposes the HTTP/FleetOps API while avoiding a surprise change to live delivery/routing loops.

## Den Web route

Den Web already uses `/api/*` for Den Channels. Do **not** route den-gateway FleetOps through `/api/gateway/*` on the public Den Web origin.

Use:

```text
/den-gateway-api/*  ->  http://127.0.0.1:5300/api/gateway/*
```

For the FleetOps cockpit, the runtime config should set:

```json
{
  "denGatewayApiBase": "/den-gateway-api"
}
```

Then the frontend calls:

```text
GET /den-gateway-api/fleet-ops
POST /den-gateway-api/fleet-ops/actions/{actionId}/runs
GET /den-gateway-api/fleet-ops/runs/{runId}
```

The static server must continue to proxy existing `/api/*` requests to Den Channels.

## Deploy outline

```bash
# From a clean den-gateway checkout at the reviewed commit
dotnet publish src/DenGateway.Service/DenGateway.Service.csproj -c Release -o artifacts/publish/DenGateway.Service

# Copy publish output to den-srv:/data/services/den-gateway/publish
# Install deploy/den-gateway.den-srv.service as /etc/systemd/system/den-gateway.service
sudo systemctl daemon-reload
sudo systemctl enable --now den-gateway.service
```

## Smoke checks

```bash
curl -fsS http://127.0.0.1:5300/health/live
curl -fsS http://127.0.0.1:5300/health/ready
curl -fsS http://127.0.0.1:5300/api/gateway/fleet-ops | jq '{service, actionCount: (.actions | length), serviceUnitCount: (.serviceUnits | length), discoveryDiagnostics}'

curl -fsS http://192.168.1.10:18080/den-web-build.json
curl -fsS http://192.168.1.10:18080/den-gateway-api/fleet-ops | jq '{service, actionCount: (.actions | length)}'
curl -fsS 'http://192.168.1.10:18080/api/gateway/memberships?projectId=den-web' | jq 'keys'
```

## Rollback

1. Restore the backed-up Den Web static server/config files and restart `den-web.service`.
2. Stop or disable the Gateway service:

```bash
sudo systemctl stop den-gateway.service
sudo systemctl disable den-gateway.service
```

3. Restore any previous `/data/services/den-gateway/publish` backup if replacing a prior deployment.
