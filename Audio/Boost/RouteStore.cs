using System.IO;
using System.Text.Json;

namespace Otto.Audio.Boost;

/// <summary>
/// Every app Otto sent to the boost output, written to disk at once. Windows remembers a per-app output
/// for the executable, so if Otto dies while boosting, the app would stay silent: the next Otto puts it
/// back (at startup for running processes, or the moment it shows up on the boost output).
/// </summary>
public sealed class RouteStore
{
    public sealed class Route
    {
        public string Exe { get; set; } = "";
        public uint Pid { get; set; }
        /// <summary>The app's own per-app output before Otto moved it (null = Windows default).</summary>
        public string? Previous { get; set; }
    }

    public string? SinkId { get; set; }
    /// <summary>Otto muted the boost output; restore this mute state when boost ends.</summary>
    public bool SinkMuteOwned { get; set; }
    public bool SinkWasMuted { get; set; }
    public List<Route> Routes { get; set; } = new();

    private static readonly string FilePath = Path.Combine(Log.Dir, "boost-routes.json");

    public static RouteStore Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<RouteStore>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { Log.Error("boost routes load", ex); }
        return new();
    }

    public void Save()
    {
        try
        {
            if (Routes.Count == 0 && !SinkMuteOwned) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) { Log.Error("boost routes save", ex); }
    }
}
