namespace HaWinServer.Core;

/// <summary>
/// Polls Home Assistant's HTTP endpoint to know when the process has actually
/// finished booting - starting the OS process is not the same as HA being up
/// (first boot installs integration dependencies and can take minutes).
/// </summary>
public sealed class HealthProbe
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    /// <summary>Single check: true if HA answers HTTP at all (any status code counts as "up").</summary>
    public static async Task<bool> IsUpAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var response = await Client.GetAsync($"http://127.0.0.1:{port}/manifest.json", ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Polls until HA responds or the overall timeout elapses. Uses a short
    /// interval while starting up (first boot can take a long time, so the
    /// overall timeout is generous) and returns as soon as a response arrives.
    /// </summary>
    public static async Task<bool> WaitUntilUpAsync(
        int port,
        TimeSpan pollInterval,
        TimeSpan overallTimeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(overallTimeout);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (await IsUpAsync(port, cts.Token))
                {
                    return true;
                }

                await Task.Delay(pollInterval, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Overall timeout (or caller cancellation) - report "not up" rather than throw.
        }

        return false;
    }
}
