#!/bin/bash
set -e

# Start the operator UI in the background (must cd so content root matches).
# Auth env vars (LOGDB_UI_PASSWORD, LOGDB_UI_USERNAME, LOGDB_API_KEY) are inherited
# from the container environment. Persist the cookie data-protection keys on the state
# volume so logins survive restarts.
cd /app/ui
ASPNETCORE_URLS=http://+:8081 \
COLLECTOR_API_URL="${COLLECTOR_API_URL:-http://localhost:8080}" \
LOGDB_DP_KEYS_DIR="${LOGDB_DP_KEYS_DIR:-/var/lib/logdb-nginx-collector/dataprotection-keys}" \
dotnet com.logdb.nginx.collector.ui.dll &

# Start the collector backend as the main process
cd /app/backend
ASPNETCORE_URLS=http://+:8080 \
exec dotnet com.logdb.nginx.collector.dll
