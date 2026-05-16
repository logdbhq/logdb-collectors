using System.IO.Pipes;

namespace com.logdb.windows.collector.Hosting;

public static class PipeProbe
{
    public static async Task<bool> IsReachableAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
