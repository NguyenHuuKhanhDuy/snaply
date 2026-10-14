using System.IO.Pipes;
using System.Text;

namespace Snaply.App.Services;

/// <summary><c>Background</c> = started at sign-in: live in the tray without opening a window.</summary>
public enum StartupCommand { Open, CaptureRegion, CaptureFullScreen, Background }

public static class StartupCommands
{
    public static StartupCommand Parse(IEnumerable<string> args)
    {
        foreach (var a in args)
        {
            switch (a.ToLowerInvariant())
            {
                case "--capture-region": return StartupCommand.CaptureRegion;
                case "--capture-fullscreen": return StartupCommand.CaptureFullScreen;
                case "--open": return StartupCommand.Open;
                case "--background": return StartupCommand.Background;
            }
        }
        return StartupCommand.Open;
    }
}

/// <summary>Ensures one running instance and forwards commands from later launches over a per-user named pipe.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    private SingleInstance(Mutex? mutex, bool isFirst, string pipeName)
    {
        _mutex = mutex; IsFirst = isFirst; _pipeName = pipeName;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire(string? scope = null)
    {
        var id = SanitizeId(scope ?? Environment.UserName);
        var mutex = new Mutex(true, $"Snaply.Instance.{id}", out var created);
        return new SingleInstance(mutex, created, $"Snaply.Pipe.{id}");
    }

    private static string SanitizeId(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Starts listening for commands from secondary launches.</summary>
    public void Listen(Action<StartupCommand> onCommand)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (Enum.TryParse<StartupCommand>(line, out var cmd)) onCommand(cmd);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* client disconnected early; keep listening */ }
            }
        }, ct);
    }

    public bool SendToRunning(StartupCommand command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(command.ToString());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return false; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsFirst) { try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex?.Dispose();
    }
}
