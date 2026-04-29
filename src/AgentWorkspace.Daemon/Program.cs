using System;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Daemon;
using AgentWorkspace.Daemon.Channels;

var shutdownCts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    if (shutdownCts.IsCancellationRequested) return;
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("[awtd] Ctrl+C received — initiating graceful shutdown.");
    shutdownCts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (!shutdownCts.IsCancellationRequested)
    {
        shutdownCts.Cancel();
    }
};

var options = new DaemonHostOptions
{
    OnClientAuthenticated = args =>
        Console.WriteLine($"[awtd] client authenticated on {args.Pipe.GetType().Name}"),
    OnClientRejected = args =>
        Console.WriteLine($"[awtd] client rejected: {args.Reason}"),
};

await using var host = new DaemonHost(options);

try
{
    await host.StartAsync(shutdownCts.Token).ConfigureAwait(false);
    Console.WriteLine($"[awtd] listening on \\\\.\\pipe\\{host.ResolvedPipeName}");
    Console.WriteLine($"[awtd] session token written to {host.TokenPath}");
    Console.WriteLine("[awtd] press Ctrl+C to stop.");

    try
    {
        await Task.Delay(Timeout.Infinite, shutdownCts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // expected on Ctrl+C / ProcessExit
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[awtd] fatal: {ex}");
    return 1;
}

Console.WriteLine("[awtd] stopped.");
return 0;
