#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_DIR="${OUTPUT_DIR:-$ROOT_DIR/artifacts/publish/DenGateway.Service}"

cd "$ROOT_DIR"

dotnet restore DenGateway.slnx
dotnet test DenGateway.slnx --configuration "$CONFIGURATION" --no-restore
dotnet publish src/DenGateway.Service/DenGateway.Service.csproj \
  --configuration "$CONFIGURATION" \
  --output "$OUTPUT_DIR" \
  --no-restore

printf 'Published DenGateway.Service to %s\n' "$OUTPUT_DIR"
printf 'Run locally with:\n  ASPNETCORE_ENVIRONMENT=Production %s/DenGateway.Service --urls http://127.0.0.1:5300\n' "$OUTPUT_DIR"
