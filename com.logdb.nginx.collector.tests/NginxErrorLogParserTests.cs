using com.logdb.nginx.collector.Models;
using com.logdb.nginx.collector.Parsing;

namespace com.logdb.nginx.collector.tests;

public class NginxErrorLogParserTests
{
    [Fact]
    public void Parse_StandardError_FileNotFound()
    {
        var line = @"2026/03/10 14:22:01 [error] 1234#5678: *99 open() ""/var/www/missing.html"" failed (2: No such file or directory), client: 192.168.1.1, server: example.com, request: ""GET /missing HTTP/1.1""";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(NginxLogType.Error, r.LogType);
        Assert.Equal("error", r.Severity);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(5678, r.Tid);
        Assert.Equal(99, r.ConnectionId);
        Assert.Equal(new DateTime(2026, 3, 10, 14, 22, 1, DateTimeKind.Utc), r.Timestamp);
        Assert.Contains("open()", r.Message);
        Assert.Null(r.Upstream);
    }

    [Fact]
    public void Parse_UpstreamError()
    {
        var line = @"2026/03/10 14:22:01 [error] 1234#5678: *100 connect() failed (111: Connection refused) while connecting to upstream, client: 10.0.0.1, server: api.example.com, request: ""POST /api/data HTTP/1.1"", upstream: ""http://127.0.0.1:3000/api/data"", host: ""api.example.com""";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("error", r.Severity);
        Assert.Equal(100, r.ConnectionId);
        Assert.Equal("http://127.0.0.1:3000/api/data", r.Upstream);
    }

    [Fact]
    public void Parse_Warning()
    {
        var line = @"2026/03/10 14:22:01 [warn] 1000#0: *50 an upstream response is buffered to a temporary file /var/cache/nginx/proxy_temp/1/00/0000000001";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("warn", r.Severity);
        Assert.Equal(1000, r.Pid);
        Assert.Equal(0, r.Tid);
        Assert.Equal(50, r.ConnectionId);
    }

    [Fact]
    public void Parse_CritLevel()
    {
        var line = @"2026/03/10 14:22:01 [crit] 999#0: *1 SSL_do_handshake() failed (SSL: error:14094410:SSL routines:ssl3_read_bytes:sslv3 alert handshake failure)";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("crit", r.Severity);
        Assert.Equal(1, r.ConnectionId);
    }

    [Fact]
    public void Parse_EmergLevel()
    {
        var line = @"2026/03/10 14:22:01 [emerg] 1#0: bind() to 0.0.0.0:80 failed (98: Address already in use)";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("emerg", r.Severity);
        Assert.Equal(1, r.Pid);
        // No connection ID for startup errors
        Assert.Null(r.ConnectionId);
    }

    [Fact]
    public void Parse_NoticeLevel()
    {
        var line = @"2026/03/10 14:22:01 [notice] 1#0: signal process started";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("notice", r.Severity);
        Assert.Null(r.ConnectionId);
        Assert.Contains("signal process started", r.Message);
    }

    [Fact]
    public void Parse_AlertLevel()
    {
        var line = @"2026/03/10 14:22:01 [alert] 2#0: worker process 1234 exited on signal 11";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("alert", r.Severity);
    }

    [Fact]
    public void Parse_NoConnectionId()
    {
        var line = @"2026/03/10 14:22:01 [error] 1234#0: open() ""/etc/nginx/conf.d/default.conf"" failed (2: No such file or directory)";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Null(r.ConnectionId);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(0, r.Tid);
    }

    [Fact]
    public void Parse_LargePidTid()
    {
        var line = @"2026/03/10 14:22:01 [error] 99999#12345: *999999 some error";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(99999, r.Pid);
        Assert.Equal(12345, r.Tid);
        Assert.Equal(999999, r.ConnectionId);
    }

    [Fact]
    public void Parse_EmptyLine_ReturnsNull()
    {
        Assert.Null(NginxErrorLogParser.Parse(""));
        Assert.Null(NginxErrorLogParser.Parse("   "));
        Assert.Null(NginxErrorLogParser.Parse(null!));
    }

    [Fact]
    public void Parse_MalformedLine_ReturnsNull()
    {
        Assert.Null(NginxErrorLogParser.Parse("this is not an error log"));
        Assert.Null(NginxErrorLogParser.Parse(@"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200 15 ""-"" ""curl/7.88.1"""));
        Assert.Null(NginxErrorLogParser.Parse("random garbage"));
    }

    [Fact]
    public void Parse_PartialLine_ReturnsNull()
    {
        // Missing the pid#tid section
        Assert.Null(NginxErrorLogParser.Parse("2026/03/10 14:22:01 [error] some message"));
    }

    [Fact]
    public void Parse_MultipleUpstreamReferences()
    {
        var line = @"2026/03/10 14:22:01 [error] 1234#0: *50 upstream timed out (110: Connection timed out) while reading response header from upstream, client: 10.0.0.1, server: example.com, request: ""GET /slow HTTP/1.1"", upstream: ""http://backend:8080/slow"", host: ""example.com""";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("http://backend:8080/slow", r.Upstream);
    }

    [Fact]
    public void Parse_DebugLevel()
    {
        var line = @"2026/03/10 14:22:01 [debug] 1234#0: *1 http process request line";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("debug", r.Severity);
    }

    [Fact]
    public void Parse_PreservesFullMessageLine()
    {
        var line = @"2026/03/10 14:22:01 [error] 1234#5678: *99 open() ""/var/www/missing.html"" failed (2: No such file or directory)";
        var r = NginxErrorLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(line, r.Message);
    }
}
