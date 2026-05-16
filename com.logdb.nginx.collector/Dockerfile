# LogDB Nginx Collector. Packages backend API and operator UI into a single image.
#
# Build (from LogDB.Exporters/ directory):
#   docker build -f com.logdb.nginx.collector/Dockerfile \
#     --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ) \
#     --build-arg COMMIT_HASH=$(git rev-parse --short HEAD) \
#     -t logdb/nginx-collector:rc1 .
#
# Run:
#   docker run -d -p 8080:8080 -p 8081:8081 \
#     -v /var/log/nginx:/var/log/nginx:ro \
#     logdb/nginx-collector:rc1
#
# Ports:
#   8080  Collector API + health endpoints
#   8081  Operator dashboard (Blazor UI)

# Build: backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-backend
WORKDIR /src
COPY NuGet.Config ./
COPY com.logdb.nginx.collector/*.csproj ./
RUN dotnet restore
COPY com.logdb.nginx.collector/. .
RUN dotnet publish -c Release -o /app

# Build: operator UI
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-ui
WORKDIR /src
COPY NuGet.Config ./
COPY com.logdb.nginx.collector.ui/*.csproj ./
RUN dotnet restore
COPY com.logdb.nginx.collector.ui/. .
RUN dotnet publish -c Release -o /app

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG BUILD_DATE=unknown
ARG COMMIT_HASH=unknown

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /var/lib/logdb-nginx-collector

WORKDIR /app
COPY --from=build-backend /app ./backend
COPY --from=build-ui /app ./ui
COPY com.logdb.nginx.collector/entrypoint.sh .
RUN sed -i 's/\r$//' entrypoint.sh && chmod +x entrypoint.sh

ENV ASPNETCORE_ENVIRONMENT=Docker
ENV LOGDB_BUILD_DATE=${BUILD_DATE}
ENV LOGDB_COMMIT_HASH=${COMMIT_HASH}

EXPOSE 8080 8081

HEALTHCHECK --interval=15s --timeout=3s --start-period=10s \
  CMD curl -sf http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["./entrypoint.sh"]
