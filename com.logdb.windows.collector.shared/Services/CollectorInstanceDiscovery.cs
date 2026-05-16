using System.Text.Json;
using System.Text.Json.Serialization;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.shared.Services;

public static class CollectorInstanceDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static CollectorInstanceDiscovery()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static string ResolvePipeName(CollectorInstanceMode mode)
    {
        return ControlChannelDefaults.ResolvePipeName(mode);
    }

    public static string ResolveRunInfoPath(CollectorInstanceMode mode)
    {
        var fileName = mode == CollectorInstanceMode.Service
            ? "run.service.json"
            : "run.console.json";
        return Path.Combine(CollectorPathDefaults.BaseDirectory, fileName);
    }

    public static bool TryParseRunInfo(string json, out CollectorRuntimeInfoDto? runtimeInfo)
    {
        runtimeInfo = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            runtimeInfo = JsonSerializer.Deserialize<CollectorRuntimeInfoDto>(json, JsonOptions);
            return runtimeInfo != null
                   && !string.IsNullOrWhiteSpace(runtimeInfo.PipeName)
                   && runtimeInfo.ProcessId > 0;
        }
        catch
        {
            return false;
        }
    }
}
