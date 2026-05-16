using System.Globalization;
using System.Text.RegularExpressions;
using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Parsing;

/// <summary>
/// Parses Nginx error log format:
/// yyyy/MM/dd HH:mm:ss [level] pid#tid: *connection_id message
///
/// Example:
/// 2026/03/10 14:22:01 [error] 1234#5678: *99 open() "/var/www/missing.html" failed (2: No such file or directory), client: 192.168.1.1, server: example.com, request: "GET /missing HTTP/1.1"
/// </summary>
public static partial class NginxErrorLogParser
{
    [GeneratedRegex(
        @"^(?<time>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+\[(?<level>\w+)\]\s+(?<pid>\d+)#(?<tid>\d+):\s+(?:\*(?<cid>\d+)\s+)?(?<message>.+)$",
        RegexOptions.Compiled)]
    private static partial Regex ErrorLogRegex();

    private static readonly Regex UpstreamRegex = new(
        @"upstream:\s+""(?<upstream>[^""]+)""",
        RegexOptions.Compiled);

    public static NginxLogRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = ErrorLogRegex().Match(line);
        if (!match.Success)
            return null;

        var record = new NginxLogRecord
        {
            LogType = NginxLogType.Error,
            Message = line,
            Severity = match.Groups["level"].Value
        };

        // Parse timestamp
        var timeStr = match.Groups["time"].Value;
        if (DateTime.TryParseExact(timeStr, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
        {
            record.Timestamp = ts;
        }
        else
        {
            record.Timestamp = DateTime.UtcNow;
        }

        // PID/TID
        if (int.TryParse(match.Groups["pid"].Value, out var pid))
            record.Pid = pid;
        if (int.TryParse(match.Groups["tid"].Value, out var tid))
            record.Tid = tid;

        // Connection ID
        if (match.Groups["cid"].Success && long.TryParse(match.Groups["cid"].Value, out var cid))
            record.ConnectionId = cid;

        // Extract upstream info if present
        var msgText = match.Groups["message"].Value;
        var upstreamMatch = UpstreamRegex.Match(msgText);
        if (upstreamMatch.Success)
            record.Upstream = upstreamMatch.Groups["upstream"].Value;

        return record;
    }
}
