using System.Text.Json;
using System.Text.Json.Serialization;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.shared.Services;

public static class CollectorRuntimeInfoPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static CollectorRuntimeInfoPersistence()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static string ResolvePath(CollectorInstanceMode mode)
    {
        return CollectorInstanceDiscovery.ResolveRunInfoPath(mode);
    }

    public static async Task SaveAsync(
        CollectorRuntimeInfoDto runtimeInfo,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        var infoPath = path ?? ResolvePath(runtimeInfo.Mode);
        var directory = Path.GetDirectoryName(infoPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = infoPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, runtimeInfo, JsonOptions, cancellationToken);
        }

        if (File.Exists(infoPath))
        {
            File.Replace(tempPath, infoPath, null, true);
            return;
        }

        File.Move(tempPath, infoPath);
    }

    public static bool TryLoad(CollectorInstanceMode mode, out CollectorRuntimeInfoDto? runtimeInfo, string? path = null)
    {
        runtimeInfo = null;
        var infoPath = path ?? ResolvePath(mode);
        if (!File.Exists(infoPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(infoPath);
            return CollectorInstanceDiscovery.TryParseRunInfo(json, out runtimeInfo)
                   && runtimeInfo != null
                   && runtimeInfo.Mode == mode;
        }
        catch
        {
            return false;
        }
    }

    public static void Remove(CollectorInstanceMode mode, string? path = null)
    {
        var infoPath = path ?? ResolvePath(mode);
        if (File.Exists(infoPath))
        {
            File.Delete(infoPath);
        }
    }
}
