FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
ARG BUILD_DATE=unknown
ARG COMMIT_HASH=unknown
WORKDIR /src
COPY NuGet.Config *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
ARG BUILD_DATE=unknown
ARG COMMIT_HASH=unknown
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /var/lib/logdb-collector

COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Docker
ENV ASPNETCORE_URLS=http://+:8080
ENV LOGDB_BUILD_DATE=${BUILD_DATE}
ENV LOGDB_COMMIT_HASH=${COMMIT_HASH}

EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=3s --start-period=10s \
  CMD curl -sf http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "com.logdb.docker.collector.dll"]
