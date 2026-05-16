using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.shared.Services;

public static class CollectorConfigPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<CollectorConfigDto> LoadAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        var configPath = path ?? CollectorPathDefaults.ConfigPath;
        if (!File.Exists(configPath))
        {
            return new CollectorConfigDto();
        }

        await using var stream = File.OpenRead(configPath);
        var loaded = await JsonSerializer.DeserializeAsync<CollectorConfigDto>(stream, JsonOptions, cancellationToken);
        return loaded ?? new CollectorConfigDto();
    }

    public static async Task EnsureExistsAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        var configPath = path ?? CollectorPathDefaults.ConfigPath;
        if (File.Exists(configPath))
        {
            return;
        }

        await SaveAsync(new CollectorConfigDto(), configPath, cancellationToken);
    }

    public static async Task SaveAsync(
        CollectorConfigDto config,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        var configPath = path ?? CollectorPathDefaults.ConfigPath;
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFile = configPath + ".tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }

        if (File.Exists(configPath))
        {
            File.Replace(tempFile, configPath, null, true);
            return;
        }

        File.Move(tempFile, configPath);
    }

    public static string ToJson(CollectorConfigDto config)
    {
        return JsonSerializer.Serialize(config, JsonOptions);
    }
}
