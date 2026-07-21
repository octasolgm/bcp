#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

echo "Starting Reguliq .NET stack..."
echo "  NestJS API  → http://localhost:4000 (Landing AI bridge)"
echo "  .NET API    → http://localhost:5100"
echo "  Angular UI  → http://localhost:3002"

# NestJS for Landing AI Phase 1
npm run dev:api &
API_PID=$!

sleep 3

# .NET API
cd apps/reguliq-dotnet/src/Reguliq.Api
dotnet run &
DOTNET_PID=$!

sleep 4

# Angular
cd "$ROOT/apps/reguliq-web"
if [ ! -d node_modules ]; then npm install; fi
npx ng serve --port 3002 --host localhost &
ANG_PID=$!

trap 'kill $API_PID $DOTNET_PID $ANG_PID 2>/dev/null || true' EXIT INT TERM

echo ""
echo "Reguliq .NET stack running:"
echo "  Dashboard: http://localhost:3002/dashboard"
echo "  Dual Verify: http://localhost:3002/dual-verify"
wait
