using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.ui.Services;

public sealed class CollectorEndpointInfo
{
    public CollectorInstanceMode Mode { get; init; }
    public string PipeName { get; init; } = string.Empty;
    public bool IsReachable { get; init; }
    public CollectorRuntimeInfoDto? RuntimeInfo { get; init; }
}

public sealed class CollectorDiscoverySnapshot
{
    public required ServiceQueryResult Service { get; init; }
    public required CollectorEndpointInfo ServiceEndpoint { get; init; }
    public required CollectorEndpointInfo ConsoleEndpoint { get; init; }
}

public sealed class CollectorDiscoveryService
{
    private readonly ControlChannelClient _controlChannelClient;

    public CollectorDiscoveryService(ControlChannelClient controlChannelClient)
    {
        _controlChannelClient = controlChannelClient;
    }

    public async Task<CollectorDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var serviceQuery = await ServiceControl.QueryAsync();
        var servicePipe = CollectorInstanceDiscovery.ResolvePipeName(CollectorInstanceMode.Service);
        var consolePipe = CollectorInstanceDiscovery.ResolvePipeName(CollectorInstanceMode.Console);

        var serviceReachable = await _controlChannelClient.IsEndpointAvailableAsync(
            CollectorInstanceMode.Service,
            cancellationToken: cancellationToken);
        var consoleReachable = await _controlChannelClient.IsEndpointAvailableAsync(
            CollectorInstanceMode.Console,
            cancellationToken: cancellationToken);

        CollectorRuntimeInfoPersistence.TryLoad(CollectorInstanceMode.Service, out var serviceRuntime);
        CollectorRuntimeInfoPersistence.TryLoad(CollectorInstanceMode.Console, out var consoleRuntime);

        return new CollectorDiscoverySnapshot
        {
            Service = serviceQuery,
            ServiceEndpoint = new CollectorEndpointInfo
            {
                Mode = CollectorInstanceMode.Service,
                PipeName = servicePipe,
                IsReachable = serviceReachable,
                RuntimeInfo = serviceRuntime
            },
            ConsoleEndpoint = new CollectorEndpointInfo
            {
                Mode = CollectorInstanceMode.Console,
                PipeName = consolePipe,
                IsReachable = consoleReachable,
                RuntimeInfo = consoleRuntime
            }
        };
    }
}
