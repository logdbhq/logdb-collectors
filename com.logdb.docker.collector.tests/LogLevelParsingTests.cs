using com.logdb.docker.collector.Models;
using com.logdb.docker.collector.Services;

namespace com.logdb.docker.collector.tests;

public class LogLevelParsingTests
{
    private static string? Parse(string message, string stream = "stderr")
    {
        var record = new LogRecord { Message = message, Stream = stream };
        DockerFileTailService.NormalizeLogLevel(record);
        return record.ParsedLevel;
    }

    // --- PostgreSQL (writes every severity to stderr) ---

    [Theory]
    [InlineData("LOG", "Info")]
    [InlineData("INFO", "Info")]
    [InlineData("NOTICE", "Info")]
    [InlineData("WARNING", "Warning")]
    [InlineData("ERROR", "Error")]
    [InlineData("FATAL", "Critical")]
    [InlineData("PANIC", "Critical")]
    [InlineData("DEBUG1", "Debug")]
    [InlineData("DEBUG5", "Debug")]
    public void Postgres_severity_maps_to_level(string severity, string expected)
    {
        var line = $"2026-06-05 11:33:40.438 UTC [127928] [local] admin com.motivp.e360.db {severity}:  some message";
        Assert.Equal(expected, Parse(line));
    }

    [Fact]
    public void Postgres_routine_connection_log_is_info_not_error()
    {
        // The exact line from the bug report — must NOT be Error.
        var line = "2026-06-05 11:33:40.438 UTC [127928] [local] admin com.motivp.e360.db LOG:  " +
                   "connection authorized: user=admin database=com.motivp.e360.db application_name=pg_isready";
        Assert.Equal("Info", Parse(line));
    }

    // --- logfmt / logrus (CrowdSec, Traefik, Docker daemon, ...) ---

    [Theory]
    [InlineData("trace", "Trace")]
    [InlineData("debug", "Debug")]
    [InlineData("info", "Info")]
    [InlineData("warn", "Warning")]
    [InlineData("warning", "Warning")]
    [InlineData("error", "Error")]
    [InlineData("fatal", "Critical")]
    [InlineData("panic", "Critical")]
    public void Logfmt_level_maps_to_level(string level, string expected)
    {
        var line = $"time=\"2026-06-05T11:33:41Z\" level={level} msg=\"something happened\" module=lapi";
        Assert.Equal(expected, Parse(line));
    }

    [Fact]
    public void Crowdsec_lapi_access_log_is_info_not_error()
    {
        // The exact line from the bug report — must NOT be Error.
        var line = "time=\"2026-06-05T11:33:41Z\" level=info msg=\"172.26.0.1 - [Fri, 05 Jun 2026 11:33:41 UTC] " +
                   "\\\"GET /v1/decisions/stream?startup=false HTTP/1.1 200 111.161613ms \\\"crowdsec-nginx-bouncer/v1.1.6\\\" \\\"\" module=lapi";
        Assert.Equal("Info", Parse(line));
    }

    [Fact]
    public void Logfmt_takes_first_level_token_before_msg()
    {
        // A "level=" appearing inside the message must not override the real severity.
        var line = "time=\"2026-06-05T11:33:41Z\" level=info msg=\"retrying at level=error backoff\"";
        Assert.Equal("Info", Parse(line));
    }

    [Fact]
    public void Logfmt_unrecognized_level_keyword_is_not_parsed()
    {
        // Unknown keyword should fall through (left null) rather than be guessed.
        var line = "time=\"2026-06-05T11:33:41Z\" level=verbose msg=\"hi\"";
        Assert.Null(Parse(line));
    }

    // --- .NET ConsoleLogger ---

    [Theory]
    [InlineData("info", "Info")]
    [InlineData("warn", "Warning")]
    [InlineData("fail", "Error")]
    [InlineData("crit", "Critical")]
    [InlineData("dbug", "Debug")]
    [InlineData("trce", "Trace")]
    public void DotNet_console_header_maps_to_level(string prefix, string expected)
    {
        var line = $"{prefix}: Some.Namespace.Class[0]\n      the actual log message";
        Assert.Equal(expected, Parse(line, stream: "stdout"));
    }

    // --- Fallback: nothing recognized leaves ParsedLevel null so the exporter's
    //     stream-based default applies. ---

    [Fact]
    public void Plain_text_leaves_level_unparsed()
    {
        Assert.Null(Parse("just a plain message with no recognizable level", stream: "stderr"));
    }

    [Fact]
    public void Postgres_parser_only_applies_to_stderr()
    {
        // A stdout line that merely contains "LOG:" must not be treated as a PG severity.
        var line = "2026-06-05 11:33:40.438 UTC [1] x x x LOG:  hi";
        Assert.Null(Parse(line, stream: "stdout"));
    }
}
