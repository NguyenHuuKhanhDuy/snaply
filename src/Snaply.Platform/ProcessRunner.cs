using System.Diagnostics;

namespace Snaply.Platform;

internal static class ProcessRunner
{
    public sealed record RunResult(bool Started, int ExitCode, string StdErr);

    public static bool IsOnPath(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir, tool))) return true; }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return false;
    }

    public static async Task<RunResult> RunAsync(string file, IEnumerable<string> args, byte[]? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = stdin is not null,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new RunResult(false, -1, "process did not start");

            var err = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            if (stdin is not null)
            {
                await p.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
                p.StandardInput.Close();
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return new RunResult(true, -1, "timed out");
            }
            return new RunResult(true, p.ExitCode, await err.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new RunResult(false, -1, ex.Message);
        }
    }

    public static void Launch(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
}
