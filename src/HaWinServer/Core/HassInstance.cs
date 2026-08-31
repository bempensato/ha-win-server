namespace HaWinServer.Core;

/// <summary>
/// One running Home Assistant instance as the app sees it: its persisted
/// settings paired with the supervisor driving its container. Holds the
/// per-instance conveniences (URLs) that used to live on TrayContext back
/// when there was only ever one instance.
/// </summary>
public sealed class HassInstance : IDisposable
{
    public InstanceSettings Settings { get; }
    public HassSupervisor Supervisor { get; }

    public HassInstance(InstanceSettings settings)
    {
        Settings = settings;
        Supervisor = new HassSupervisor(settings);
    }

    public string Id => Settings.Id;
    public string Name => Settings.Name;
    public HassState State => Supervisor.State;

    public string WebUiUrl => $"http://localhost:{Settings.Port}";

    public string? LanUrl
    {
        get
        {
            var ip = NetworkInfo.TryGetLanIPv4Address();
            return ip is null ? null : $"http://{ip}:{Settings.Port}";
        }
    }

    public void Dispose() => Supervisor.Dispose();
}
