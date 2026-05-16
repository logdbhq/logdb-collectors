using System.Reflection;

namespace com.logdb.docker.collector.Models;

public static class BuildInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.1";

    public static string BuildDate =>
        Environment.GetEnvironmentVariable("LOGDB_BUILD_DATE") ?? "dev";

    public static string CommitHash =>
        Environment.GetEnvironmentVariable("LOGDB_COMMIT_HASH") ?? "dev";
}
