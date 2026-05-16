using System.Globalization;
using System.Text.RegularExpressions;
using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Parsing;

/// <summary>
/// Parses Nginx combined log format:
/// $remote_addr - $remote_user [$time_local] "$request" $status $body_bytes_sent "$http_referer" "$http_user_agent"
///
/// Example:
/// 192.168.1.1 - - [10/Mar/2026:14:22:01 +0000] "GET /api/health HTTP/1.1" 200 15 "-" "curl/7.88.1"
/// </summary>
public static partial class NginxAccessLogParser
{
    // Combined log format regex
    [GeneratedRegex(
        @"^(?<remote>\S+)\s+\S+\s+\S+\s+\[(?<time>[^\]]+)\]\s+""(?<request>[^""]*)""\s+(?<status>\d{3})\s+(?<bytes>\S+)\s+""(?<referer>[^""]*)""\s+""(?<agent>[^""]*)""(?:\s+(?<extra>.*))?$",
        RegexOptions.Compiled)]
    private static partial Regex CombinedLogRegex();

    private static readonly string[] TimeFormats =
    {
        "dd/MMM/yyyy:HH:mm:ss zzz",
        "dd/MMM/yyyy:HH:mm:ss K"
    };

    public static NginxLogRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = CombinedLogRegex().Match(line);
        if (!match.Success)
            return null;

        var record = new NginxLogRecord
        {
            LogType = NginxLogType.Access,
            Message = line,
            RemoteAddress = match.Groups["remote"].Value
        };

        // Parse timestamp
        var timeStr = match.Groups["time"].Value;
        if (DateTimeOffset.TryParseExact(timeStr, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            record.Timestamp = dto.UtcDateTime;
        }
        else
        {
            record.Timestamp = DateTime.UtcNow;
        }

        // Parse request line: "METHOD /path HTTP/version"
        var requestLine = match.Groups["request"].Value;
        var requestParts = requestLine.Split(' ', 3);
        if (requestParts.Length >= 1) record.Method = requestParts[0];
        if (requestParts.Length >= 2) record.Path = requestParts[1];
        if (requestParts.Length >= 3) record.Protocol = requestParts[2];

        // Status code
        if (int.TryParse(match.Groups["status"].Value, out var status))
            record.StatusCode = status;

        // Response bytes
        var bytesStr = match.Groups["bytes"].Value;
        if (bytesStr != "-" && long.TryParse(bytesStr, out var bytes))
            record.ResponseBytes = bytes;

        // Referer
        var referer = match.Groups["referer"].Value;
        if (referer != "-")
            record.Referer = referer;

        // User agent
        var agent = match.Groups["agent"].Value;
        if (agent != "-")
            record.UserAgent = agent;

        // Extra fields (e.g., request_time if appended)
        var extra = match.Groups["extra"].Value;
        if (!string.IsNullOrWhiteSpace(extra) && double.TryParse(extra.Trim(), CultureInfo.InvariantCulture, out var reqTime))
            record.RequestTime = reqTime;

        return record;
    }
}
