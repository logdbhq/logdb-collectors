using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.tests;

public sealed class SharedContractsUnitTests
{
    [Fact]
    public void ConfigRedaction_MasksApiKey()
    {
        var config = new CollectorConfigDto
        {
            LogDB = new LogDbConfigDto
            {
                ApiKey = "abcdefghijklmno",
                Endpoint = "https://collector.example"
            }
        };

        var redacted = CollectorConfigRedactor.CreateRedacted(config);

        Assert.NotEqual(config.LogDB.ApiKey, redacted.LogDB.ApiKey);
        Assert.DoesNotContain("abcdefgh", redacted.LogDB.ApiKey, StringComparison.OrdinalIgnoreCase);
        Assert.Contains('*', redacted.LogDB.ApiKey);
    }

    [Fact]
    public void InstanceDiscovery_ResolvesModePipeNames_AndParsesRunInfo()
    {
        var legacy = Environment.GetEnvironmentVariable(ControlChannelDefaults.LegacyPipeEnvironmentVariable);
        var service = Environment.GetEnvironmentVariable(ControlChannelDefaults.ServicePipeEnvironmentVariable);
        var console = Environment.GetEnvironmentVariable(ControlChannelDefaults.ConsolePipeEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(ControlChannelDefaults.LegacyPipeEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(ControlChannelDefaults.ServicePipeEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(ControlChannelDefaults.ConsolePipeEnvironmentVariable, null);

            Assert.Equal(
                ControlChannelDefaults.ServicePipeName,
                CollectorInstanceDiscovery.ResolvePipeName(CollectorInstanceMode.Service));
            Assert.Equal(
                ControlChannelDefaults.ConsolePipeName,
                CollectorInstanceDiscovery.ResolvePipeName(CollectorInstanceMode.Console));

            var runInfoJson = """
                              {
                                "mode": "Console",
                                "pipeName": "com.logdb.windows.collector.console",
                                "processId": 1234,
                                "startedAtUtc": "2026-02-24T10:00:00Z",
                                "configPath": "C:\\ProgramData\\LogDB\\collector\\appsettings.json"
                              }
                              """;

            var parsed = CollectorInstanceDiscovery.TryParseRunInfo(runInfoJson, out var runtimeInfo);
            Assert.True(parsed);
            Assert.NotNull(runtimeInfo);
            Assert.Equal(CollectorInstanceMode.Console, runtimeInfo!.Mode);
            Assert.Equal(1234, runtimeInfo.ProcessId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ControlChannelDefaults.LegacyPipeEnvironmentVariable, legacy);
            Environment.SetEnvironmentVariable(ControlChannelDefaults.ServicePipeEnvironmentVariable, service);
            Environment.SetEnvironmentVariable(ControlChannelDefaults.ConsolePipeEnvironmentVariable, console);
        }
    }

    [Fact]
    public void CollectorStatusDto_SerializesAndDeserializes()
    {
        var input = new CollectorStatusDto
        {
            ServiceName = "LogDB Windows Collector",
            InstanceMode = CollectorInstanceMode.Console,
            ControlPipeName = ControlChannelDefaults.ConsolePipeName,
            ProcessId = 5012,
            ConfigPath = "C:\\ProgramData\\LogDB\\collector\\appsettings.json",
            Modules =
            [
                new ModuleStatusDto
                {
                    Name = "Metrics",
                    Enabled = true,
                    State = "Running",
                    SentCount = 10,
                    FailedCount = 1
                }
            ]
        };

        var json = JsonSerializer.Serialize(input, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var output = JsonSerializer.Deserialize<CollectorStatusDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(output);
        Assert.Equal(input.InstanceMode, output!.InstanceMode);
        Assert.Equal(input.ControlPipeName, output.ControlPipeName);
        Assert.Equal(input.ProcessId, output.ProcessId);
        Assert.Single(output.Modules);
        Assert.Equal("Metrics", output.Modules[0].Name);
    }

    [Fact]
    public void CollectorFailureDto_SerializesAndDeserializes()
    {
        var input = new List<CollectorFailureDto>
        {
            new()
            {
                TimestampUtc = new DateTime(2026, 6, 7, 11, 0, 51, DateTimeKind.Utc),
                Module = "Metrics",
                Error = "gRPC channel unreachable: 503"
            }
        };

        var json = JsonSerializer.Serialize(input, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var output = JsonSerializer.Deserialize<List<CollectorFailureDto>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(output);
        var failure = Assert.Single(output!);
        Assert.Equal(input[0].TimestampUtc, failure.TimestampUtc);
        Assert.Equal("Metrics", failure.Module);
        Assert.Equal("gRPC channel unreachable: 503", failure.Error);
    }
}
