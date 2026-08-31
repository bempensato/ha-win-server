using System.Diagnostics;
using System.Text;

namespace HaWinServer.Core;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Thin wrapper around Process for running external tools (wsl.exe, mainly)
/// with captured/streamed output. Centralized here
/// so every caller gets the same "no console window, no shell, UTF-8" setup.
/// </summary>
public static class ProcRunner
{
    /// <summary>
    /// Runs a process to completion, capturing stdout/stderr. Optionally streams
    /// each line to <paramref name="onOutputLine"/> as it arrives (for progress UI).
    /// </summary>
    public static async Task<ProcResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IDictionary<string, string?>? extraEnvironment = null,
        string? workingDirectory = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                if (value is null)
                {
                    psi.Environment.Remove(key);
                }
                else
                {
                    psi.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdOut.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdErr.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort: process may have already exited.
            }
        }))
        {
            await process.WaitForExitAsync(cancellationToken);
        }

        return new ProcResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}
