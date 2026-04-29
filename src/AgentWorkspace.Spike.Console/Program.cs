// AgentWorkspace.Spike.Console
//
// Smallest possible exerciser for AgentWorkspace.ConPTY. Hosts a child shell under a ConPTY,
// pumps user keystrokes into it, prints the child's output to stdout. This is the Day 1–2 spike
// from DESIGN.md §10: validates that the runtime can drive pwsh / cmd / wsl correctly before any
// UI is built.
//
// Usage:
//   awt-spike                # launches pwsh.exe
//   awt-spike cmd /K echo hi # launches an arbitrary command
//
// Press Ctrl+C to send SIGINT to the child. Press Ctrl+] (GS, 0x1D) to terminate the spike.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.ConPTY;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.Error.WriteLine("This spike requires Windows (ConPTY).");
    return 1;
}

return await RunAsync(args).ConfigureAwait(false);

[SupportedOSPlatform("windows")]
static async Task<int> RunAsync(string[] args)
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;

    string command = args.Length > 0 ? args[0] : ResolveDefaultShell();
    var arguments = args.Length > 1 ? args[1..] : Array.Empty<string>();

    short cols = (short)Math.Clamp(Console.WindowWidth, 20, 32767);
    short rows = (short)Math.Clamp(Console.WindowHeight, 5, 32767);

    var pane = new PseudoConsoleProcess(PaneId.New());
    pane.Exited += (_, code) =>
        Console.Error.WriteLine($"\n[spike] child exited with code {code}");

    using var ctsApp = new CancellationTokenSource();

    Console.CancelKeyPress += async (_, e) =>
    {
        // Forward Ctrl+C to the child rather than killing the spike.
        e.Cancel = true;
        try { await pane.SignalAsync(PtySignal.Interrupt, ctsApp.Token).ConfigureAwait(false); }
        catch { /* best effort */ }
    };

    try
    {
        await pane.StartAsync(
            new PaneStartOptions(
                Command: command,
                Arguments: arguments,
                WorkingDirectory: Environment.CurrentDirectory,
                Environment: null,
                InitialColumns: cols,
                InitialRows: rows),
            ctsApp.Token).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[spike] failed to start: {ex.Message}");
        await pane.DisposeAsync().ConfigureAwait(false);
        return 2;
    }

    Console.Error.WriteLine($"[spike] launched pid={pane.ProcessId} ({command}). Ctrl+] to quit.");

    var outputTask = PumpOutputAsync(pane, ctsApp.Token);
    var inputTask = PumpInputAsync(pane, ctsApp);
    var resizeTask = WatchResizeAsync(pane, cols, rows, ctsApp.Token);
    var exitTask = WatchExitAsync(pane, ctsApp);

    await Task.WhenAny(outputTask, inputTask, exitTask).ConfigureAwait(false);

    ctsApp.Cancel();
    try { await pane.KillAsync(KillMode.Force, CancellationToken.None).ConfigureAwait(false); } catch { /* swallow */ }
    await pane.DisposeAsync().ConfigureAwait(false);

    try { await Task.WhenAll(outputTask, inputTask, resizeTask, exitTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
    catch { /* swallow shutdown noise */ }

    return 0;
}

static string ResolveDefaultShell()
{
    // pwsh.exe if available; otherwise fall back to Windows PowerShell, then cmd.exe.
    foreach (string candidate in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
    {
        string? path = SearchPath(candidate);
        if (path is not null)
        {
            return candidate;
        }
    }
    return "cmd.exe";
}

static string? SearchPath(string fileName)
{
    foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
    {
        if (string.IsNullOrEmpty(dir))
        {
            continue;
        }
        try
        {
            string full = Path.Combine(dir, fileName);
            if (File.Exists(full))
            {
                return full;
            }
        }
        catch
        {
            // skip invalid PATH entries
        }
    }
    return null;
}

[SupportedOSPlatform("windows")]
static async Task PumpOutputAsync(PseudoConsoleProcess pane, CancellationToken ct)
{
    using var stdout = Console.OpenStandardOutput();
    try
    {
        await foreach (var chunk in pane.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await stdout.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
                await stdout.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(chunk.Data, out var seg) && seg.Array is { } arr)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(arr);
                }
            }
        }
    }
    catch (OperationCanceledException) { /* shutting down */ }
}

[SupportedOSPlatform("windows")]
static async Task PumpInputAsync(PseudoConsoleProcess pane, CancellationTokenSource ctsApp)
{
    var ct = ctsApp.Token;
    var encoding = Encoding.UTF8;

    while (!ct.IsCancellationRequested)
    {
        ConsoleKeyInfo key;
        try
        {
            // ReadKey is blocking; offload to a background thread so we can cancel.
            key = await Task.Run(() => Console.ReadKey(intercept: true), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            // stdin redirected
            return;
        }

        // Ctrl+] — quit the spike.
        if (key.KeyChar == (char)0x1D)
        {
            ctsApp.Cancel();
            return;
        }

        ReadOnlyMemory<byte> bytes = TranslateKey(key, encoding);
        if (bytes.Length == 0)
        {
            continue;
        }

        try
        {
            await pane.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return; // pane gone
        }
    }
}

static ReadOnlyMemory<byte> TranslateKey(ConsoleKeyInfo key, Encoding encoding)
{
    // Common navigation keys → ANSI / xterm escape sequences. The ConPTY child sees the same
    // sequences a real terminal would produce.
    string? sequence = key.Key switch
    {
        ConsoleKey.UpArrow => "[A",
        ConsoleKey.DownArrow => "[B",
        ConsoleKey.RightArrow => "[C",
        ConsoleKey.LeftArrow => "[D",
        ConsoleKey.Home => "[H",
        ConsoleKey.End => "[F",
        ConsoleKey.Insert => "[2~",
        ConsoleKey.Delete => "[3~",
        ConsoleKey.PageUp => "[5~",
        ConsoleKey.PageDown => "[6~",
        ConsoleKey.F1 => "OP",
        ConsoleKey.F2 => "OQ",
        ConsoleKey.F3 => "OR",
        ConsoleKey.F4 => "OS",
        ConsoleKey.Tab => "\t",
        ConsoleKey.Enter => "\r",
        ConsoleKey.Backspace => "",
        ConsoleKey.Escape => "",
        _ => null,
    };

    if (sequence is not null)
    {
        return encoding.GetBytes(sequence);
    }

    if (key.KeyChar != '\0')
    {
        return encoding.GetBytes(new[] { key.KeyChar });
    }

    return ReadOnlyMemory<byte>.Empty;
}

[SupportedOSPlatform("windows")]
static async Task WatchResizeAsync(PseudoConsoleProcess pane, short startCols, short startRows, CancellationToken ct)
{
    short prevCols = startCols;
    short prevRows = startRows;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        short cols = (short)Math.Clamp(Console.WindowWidth, 20, 32767);
        short rows = (short)Math.Clamp(Console.WindowHeight, 5, 32767);
        if (cols == prevCols && rows == prevRows)
        {
            continue;
        }

        try
        {
            await pane.ResizeAsync(cols, rows, ct).ConfigureAwait(false);
            prevCols = cols;
            prevRows = rows;
        }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException) { return; }
    }
}

[SupportedOSPlatform("windows")]
static async Task WatchExitAsync(PseudoConsoleProcess pane, CancellationTokenSource ctsApp)
{
    try { await pane.Exit.ConfigureAwait(false); }
    catch { /* swallow */ }
    ctsApp.Cancel();
}
