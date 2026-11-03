using System.Text.Json.Serialization;

namespace Pitstop.Core;

public sealed class AppRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New app";
    public string LaunchTarget { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool ForceClose { get; set; }
    public int DelaySeconds { get; set; } = 1;
    [JsonIgnore] public bool Configured => !string.IsNullOrWhiteSpace(ProcessPath) && !string.IsNullOrWhiteSpace(LaunchTarget);
    [JsonIgnore] public string Detail => !Configured ? "Choose the app on your PC" : ProcessPath;
    [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Name) ? "A" : Name[..1].ToUpperInvariant();
    [JsonIgnore] public string CloseHint => ForceClose ? "Force close enabled" : "Graceful close";
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public string Description { get; set; } = "A place for your next setup.";
    public string Category { get; set; } = "Racing";
    public List<AppRule> OpenApps { get; set; } = [];
    public List<AppRule> CloseApps { get; set; } = [];
    [JsonIgnore] public string Summary => $"{OpenApps.Count(a => a.Enabled)} to open · {CloseApps.Count(a => a.Enabled)} to close";
    [JsonIgnore] public string Glyph => Category switch { "Flight" => "✈", "Work" => "▦", "Gaming" => "▶", _ => "⚑" };
}

public sealed record ProcessIdentity(int Pid, long StartedUtcTicks);

public sealed class ProcessSnapshot
{
    public int Pid { get; set; }
    public long StartedUtcTicks { get; set; }
    public int ParentPid { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string? Arguments { get; set; }
    public bool HasWindow { get; set; }
    [JsonIgnore] public ProcessIdentity Identity => new(Pid, StartedUtcTicks);
}

public sealed class LaunchedApp
{
    public AppRule Rule { get; set; } = new();
    public DateTimeOffset RequestedAt { get; set; }
    public List<ProcessSnapshot> Tracked { get; set; } = [];
    public bool WasAlreadyRunning { get; set; }
    public bool LaunchAttempted { get; set; }
    public bool TrackingFinished { get; set; }
    public bool CloseFinished { get; set; }
}

public sealed class StoppedApp
{
    public AppRule Rule { get; set; } = new();
    public List<ProcessSnapshot> OriginalProcesses { get; set; } = [];
    public AppRule Restart { get; set; } = new();
    public bool CloseAttempted { get; set; }
    public bool RestoreFinished { get; set; }
}

public sealed record ActivityEntry(DateTimeOffset At, string Level, string Message)
{
    [JsonIgnore] public string DisplayTime => At.ToLocalTime().ToString("HH:mm:ss");
}

public sealed class SessionState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public string Status { get; set; } = "Starting";
    public List<ProcessIdentity> Baseline { get; set; } = [];
    public List<LaunchedApp> Launched { get; set; } = [];
    public List<StoppedApp> Stopped { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];
}

public sealed record CloseResult(bool Closed, string Message);
public sealed record LaunchResult(ProcessSnapshot? Process);

public interface IProcessHost
{
    Task<IReadOnlyList<ProcessSnapshot>> CaptureAsync();
    void Validate(AppRule rule, bool forLaunch);
    Task<LaunchResult> LaunchAsync(AppRule rule);
    Task<CloseResult> CloseAsync(ProcessSnapshot process, bool force);
    Task WaitAsync(TimeSpan delay);
}

public interface ISessionStore
{
    SessionState? LoadActive();
    void SaveActive(SessionState session);
    void Complete(SessionState session);
}

public static class Paths
{
    public static bool Equal(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    public static string Normalize(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\').TrimEnd('\\');
}

public static class StarterProfiles
{
    private static AppRule Sample(string name) => new() { Name = name, Enabled = false };
    private static List<AppRule> Background() => [Sample("Plex Media Server"), Sample("Samsung Notes")];
    public static List<Profile> Create() =>
    [
        new() { Name = "iRacing", Description = "Your grid. Your tools. Ready to race.", Category = "Racing",
            OpenApps = [Sample("Trading Paints"), Sample("RaceLab"), Sample("OpenKneeboard"), Sample("SimHub"), Sample("Marvin’s AIRA"), Sample("Virtual Desktop Streamer"), Sample("iRacing UI")], CloseApps = Background() },
        new() { Name = "Assetto Corsa Competizione", Description = "Everything you need for your next stint.", Category = "Racing",
            OpenApps = [Sample("SimHub"), Sample("OpenKneeboard"), Sample("Virtual Desktop Streamer"), Sample("Assetto Corsa Competizione")], CloseApps = Background() },
        new() { Name = "Flight Simulator", Description = "From desktop to cockpit in one click.", Category = "Flight",
            OpenApps = [Sample("OpenKneeboard"), Sample("Virtual Desktop Streamer"), Sample("Flight Simulator")], CloseApps = Background() }
    ];
}
