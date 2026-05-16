namespace com.logdb.windows.iis.Models;

/// <summary>
/// Represents a parsed line from an IIS W3C log file.
/// </summary>
public class IISLogEntry
{
    public DateTime Timestamp { get; set; }
    
    /// <summary>Server IP address (s-ip)</summary>
    public string? ServerIp { get; set; }
    
    /// <summary>HTTP method (cs-method): GET, POST, etc.</summary>
    public string? Method { get; set; }
    
    /// <summary>URI stem (cs-uri-stem): The requested path</summary>
    public string? UriStem { get; set; }
    
    /// <summary>URI query (cs-uri-query): Query string parameters</summary>
    public string? UriQuery { get; set; }
    
    /// <summary>Server port (s-port)</summary>
    public int ServerPort { get; set; }
    
    /// <summary>Username (cs-username)</summary>
    public string? Username { get; set; }
    
    /// <summary>Client IP address (c-ip)</summary>
    public string? ClientIp { get; set; }
    
    /// <summary>User agent (cs(User-Agent))</summary>
    public string? UserAgent { get; set; }
    
    /// <summary>Referer (cs(Referer))</summary>
    public string? Referer { get; set; }
    
    /// <summary>HTTP status code (sc-status)</summary>
    public int StatusCode { get; set; }
    
    /// <summary>HTTP substatus code (sc-substatus)</summary>
    public int SubStatus { get; set; }
    
    /// <summary>Win32 status (sc-win32-status)</summary>
    public int Win32Status { get; set; }
    
    /// <summary>Time taken in milliseconds (time-taken)</summary>
    public int TimeTaken { get; set; }
    
    /// <summary>Bytes sent (sc-bytes)</summary>
    public long BytesSent { get; set; }
    
    /// <summary>Bytes received (cs-bytes)</summary>
    public long BytesReceived { get; set; }
    
    /// <summary>Protocol version (cs-version)</summary>
    public string? ProtocolVersion { get; set; }
    
    /// <summary>Host header (cs-host)</summary>
    public string? Host { get; set; }
    
    /// <summary>Cookie (cs(Cookie))</summary>
    public string? Cookie { get; set; }
    
    /// <summary>Site name (s-sitename)</summary>
    public string? SiteName { get; set; }
    
    /// <summary>Server name (s-computername)</summary>
    public string? ServerName { get; set; }
    
    /// <summary>Source log file path</summary>
    public string? SourceFile { get; set; }
    
    /// <summary>Line number in source file</summary>
    public long LineNumber { get; set; }
    
    /// <summary>All additional fields not explicitly mapped</summary>
    public Dictionary<string, string> AdditionalFields { get; set; } = new();
}
