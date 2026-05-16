# LogDB Windows Event Viewer Exporter Service

Windows service that automatically exports Windows Event Viewer logs to the LogDB platform.

## Installation

### 1. Prerequisites

- .NET 10.0 Runtime
- Windows Server or Windows 10/11
- Administrator privileges (for reading Security log)
- Network access to LogDB service and PostgreSQL database

### 2. Configuration

Before installing the service, configure `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "PostgresConnection": "Host=YOUR_DB_HOST;Port=5432;Username=YOUR_USER;Password=YOUR_PASS;Database=YOUR_DB"
  },
  "LogDB": {
    "ServiceUrl": "",
    "ApiKey": "YOUR_API_KEY_HERE"
  },
  "Server": {
    "ServerName": "SERVER-01",
    "ServerEnvironment": "Production",
    "DefaultCollection": "windows-events",
    "DefaultApplicationName": "Windows Event Viewer",
    "DefaultLabels": [ "event-viewer", "windows", "server-01" ],
    "AccountId": 1
  }
}
```

**Important Configuration Fields:**

- **PostgresConnection**: Database connection string for retrieving configurations
- **LogDB:ServiceUrl**: (Optional) Your LogDB gRPC service URL. Leave empty to use discovery service (`https://discovery.logdb.site/resolve/grpc-logger`)
- **LogDB:ApiKey**: Your LogDB API key (required)
- **Server:ServerName**: Unique identifier for this server (e.g., "WEB-SERVER-01", "DB-SERVER-02")
- **Server:ServerEnvironment**: Default environment (Production, Staging, Development)
- **Server:DefaultCollection**: Default collection name for logs
- **Server:DefaultLabels**: Labels to add to all logs from this server
- **Server:AccountId**: Your LogDB account ID

### 3. Build

```bash
dotnet build -c Release
dotnet publish -c Release -o ./publish
```

### 4. Install as Windows Service

```powershell
# Install service
sc.exe create "LogDBEventViewerExporter" binPath="C:\Path\To\publish\com.logdb.eventviewer.exe" start=auto

# Configure service account (optional, for Security log access)
sc.exe config "LogDBEventViewerExporter" obj= "NT AUTHORITY\SYSTEM"

# Start service
sc.exe start "LogDBEventViewerExporter"

# Check status
sc.exe query "LogDBEventViewerExporter"
```

### 5. Verify Installation

Check Windows Event Viewer -> Applications and Services Logs -> LogDB Event Viewer Exporter for service logs.

## Multi-Server Deployment

Each server should have its own `appsettings.json` with:

1. **Unique ServerName**: Identifies the server in logs
2. **Server-specific Labels**: Helps filter logs by server
3. **Same AccountId**: All servers use the same LogDB account
4. **Same Database Connection**: All servers read configurations from the same database

### Example Multi-Server Setup

**Server 1 (Web Server):**
```json
{
  "Server": {
    "ServerName": "WEB-SERVER-01",
    "ServerEnvironment": "Production",
    "DefaultCollection": "windows-events",
    "DefaultLabels": [ "event-viewer", "windows", "web-server" ],
    "AccountId": 1
  }
}
```

**Server 2 (Database Server):**
```json
{
  "Server": {
    "ServerName": "DB-SERVER-01",
    "ServerEnvironment": "Production",
    "DefaultCollection": "windows-events",
    "DefaultLabels": [ "event-viewer", "windows", "db-server" ],
    "AccountId": 1
  }
}
```

## Configuration via Web UI

After installation, configure export settings via the LogDB Web UI:

1. Navigate to `/event-viewer` in the web interface
2. Create a new configuration
3. Set export interval, log sources, event levels, etc.
4. The service will automatically pick up the configuration

## How It Works

1. Service starts and reads `appsettings.json`
2. Every minute, service polls database for active configurations
3. For each configuration:
   - Checks if export interval has elapsed
   - Reads events from Windows Event Viewer
   - Applies filters (levels, sources, conditions)
   - Converts to LogDB format
   - Sends to LogDB via gRPC
   - Updates last export timestamp

## Troubleshooting

### Service Won't Start
- Check Windows Event Viewer for errors
- Verify `appsettings.json` is valid JSON
- Ensure database connection string is correct
- Check file permissions

### No Events Exported
- Verify configuration is enabled in database
- Check that log sources exist (Application, System, Security)
- Verify API key is correct
- Check service logs in Windows Event Viewer

### Permission Errors
- Service may need admin rights for Security log
- Run service as Local System account
- Grant "Log on as a service" right

### Database Connection Errors
- Verify PostgreSQL connection string
- Check network connectivity
- Ensure database is accessible from server

## Uninstallation

```powershell
# Stop service
sc.exe stop "LogDBEventViewerExporter"

# Delete service
sc.exe delete "LogDBEventViewerExporter"
```

## Files

- `appsettings.json` - Main configuration file (configure before installation)
- `appsettings.Example.json` - Example configuration template
- `com.logdb.eventviewer.exe` - Service executable

## Support

For issues or questions, check the LogDB documentation or contact support.

