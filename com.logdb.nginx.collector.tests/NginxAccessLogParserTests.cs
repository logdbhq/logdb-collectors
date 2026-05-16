using com.logdb.nginx.collector.Models;
using com.logdb.nginx.collector.Parsing;

namespace com.logdb.nginx.collector.tests;

public class NginxAccessLogParserTests
{
    [Fact]
    public void Parse_StandardCombinedFormat_200()
    {
        var line = @"192.168.1.1 - - [10/Mar/2026:14:22:01 +0000] ""GET /api/health HTTP/1.1"" 200 15 ""-"" ""curl/7.88.1""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(NginxLogType.Access, r.LogType);
        Assert.Equal("192.168.1.1", r.RemoteAddress);
        Assert.Equal("GET", r.Method);
        Assert.Equal("/api/health", r.Path);
        Assert.Equal("HTTP/1.1", r.Protocol);
        Assert.Equal(200, r.StatusCode);
        Assert.Equal(15, r.ResponseBytes);
        Assert.Null(r.Referer);         // "-" should be null
        Assert.Equal("curl/7.88.1", r.UserAgent);
        Assert.Null(r.RequestTime);
        Assert.Equal(new DateTime(2026, 3, 10, 14, 22, 1, DateTimeKind.Utc), r.Timestamp);
        Assert.Equal(line, r.Message);
    }

    [Fact]
    public void Parse_404_WithReferer()
    {
        var line = @"10.0.0.5 - john [10/Mar/2026:08:15:30 +0000] ""GET /missing-page HTTP/1.1"" 404 162 ""https://example.com/home"" ""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(404, r.StatusCode);
        Assert.Equal(162, r.ResponseBytes);
        Assert.Equal("https://example.com/home", r.Referer);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", r.UserAgent);
        Assert.Equal("10.0.0.5", r.RemoteAddress);
    }

    [Fact]
    public void Parse_500_ServerError()
    {
        var line = @"172.16.0.1 - - [05/Jan/2026:23:59:59 +0000] ""POST /api/submit HTTP/2.0"" 500 0 ""-"" ""Python-urllib/3.11""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/api/submit", r.Path);
        Assert.Equal("HTTP/2.0", r.Protocol);
        Assert.Equal(500, r.StatusCode);
        Assert.Equal(0, r.ResponseBytes);
    }

    [Fact]
    public void Parse_301_Redirect()
    {
        var line = @"192.168.1.50 - - [15/Jun/2026:12:00:00 +0300] ""GET /old-path HTTP/1.1"" 301 169 ""-"" ""Googlebot/2.1""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(301, r.StatusCode);
        Assert.Equal(169, r.ResponseBytes);
        // +0300 offset: 12:00 local = 09:00 UTC
        Assert.Equal(new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc), r.Timestamp);
    }

    [Fact]
    public void Parse_WithRequestTime()
    {
        var line = @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET /slow HTTP/1.1"" 200 1024 ""-"" ""curl/7.88.1"" 0.345";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(0.345, r.RequestTime);
    }

    [Fact]
    public void Parse_MissingUserAgent()
    {
        var line = @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200 512 ""-"" ""-""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Null(r.UserAgent);
        Assert.Null(r.Referer);
    }

    [Fact]
    public void Parse_ZeroBytes()
    {
        var line = @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""HEAD / HTTP/1.1"" 200 0 ""-"" ""curl/7.88.1""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("HEAD", r.Method);
        Assert.Equal(0, r.ResponseBytes);
    }

    [Fact]
    public void Parse_BytesDash_ReturnsNull()
    {
        // Some configs log "-" for body_bytes_sent on 304
        var line = @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 304 - ""-"" ""curl/7.88.1""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal(304, r.StatusCode);
        Assert.Null(r.ResponseBytes);
    }

    [Fact]
    public void Parse_IPv6Address()
    {
        var line = @"::1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200 15 ""-"" ""curl/7.88.1""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("::1", r.RemoteAddress);
    }

    [Fact]
    public void Parse_LongQueryString()
    {
        var line = @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET /search?q=hello+world&page=1&lang=en HTTP/1.1"" 200 4096 ""https://example.com/"" ""Mozilla/5.0""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("/search?q=hello+world&page=1&lang=en", r.Path);
    }

    [Fact]
    public void Parse_EmptyLine_ReturnsNull()
    {
        Assert.Null(NginxAccessLogParser.Parse(""));
        Assert.Null(NginxAccessLogParser.Parse("   "));
        Assert.Null(NginxAccessLogParser.Parse(null!));
    }

    [Fact]
    public void Parse_MalformedLine_ReturnsNull()
    {
        Assert.Null(NginxAccessLogParser.Parse("this is not a log line"));
        Assert.Null(NginxAccessLogParser.Parse("2026/03/10 14:22:01 [error] some error log"));
        Assert.Null(NginxAccessLogParser.Parse("random garbage 123 456"));
    }

    [Fact]
    public void Parse_PartialLine_ReturnsNull()
    {
        // Truncated - missing user agent section
        Assert.Null(NginxAccessLogParser.Parse(@"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200"));
    }

    [Fact]
    public void Parse_RequestWithoutProtocol()
    {
        // Some proxied requests have just "GET /"
        var line = @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET /"" 200 15 ""-"" ""curl/7.88.1""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        Assert.Equal("GET", r.Method);
        Assert.Equal("/", r.Path);
        Assert.Null(r.Protocol);
    }

    [Fact]
    public void Parse_NegativeTimezone()
    {
        var line = @"10.0.0.1 - - [10/Mar/2026:10:00:00 -0500] ""GET / HTTP/1.1"" 200 15 ""-"" ""curl/7.88.1""";
        var r = NginxAccessLogParser.Parse(line);

        Assert.NotNull(r);
        // 10:00 at -0500 = 15:00 UTC
        Assert.Equal(new DateTime(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc), r.Timestamp);
    }
}
