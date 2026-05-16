#!/bin/bash
set -e

# Start the operator UI in the background (must cd so content root matches)
cd /app/ui
ASPNETCORE_URLS=http://+:8081 \
COLLECTOR_API_URL="${COLLECTOR_API_URL:-http://localhost:8080}" \
dotnet com.logdb.nginx.collector.ui.dll &

# Start the collector backend as the main process
cd /app/backend
ASPNETCORE_URLS=http://+:8080 \
exec dotnet com.logdb.nginx.collector.dll
