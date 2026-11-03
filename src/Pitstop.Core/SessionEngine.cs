using System.Text.Json;

namespace Pitstop.Core;

public sealed class SessionEngine
{
    private readonly IProcessHost host;
    private readonly ISessionStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    public SessionState? Current { get; private set; }
    public event Action? Changed;

    public SessionEngine(IProcessHost host, ISessionStore store)
    {
        this.host = host;
        this.store = store;
        Current = store.LoadActive();
    }

    public async Task StartAsync(Profile source)
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A session operation is already in progress.");
        var createdSession = false;
        try
        {
            if (Current is not null) throw new InvalidOperationException("End the current session before starting another profile.");
            var profile = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(source, FileStore.Json), FileStore.Json)!;
            var opens = profile.OpenApps.Where(a => a.Enabled).ToList();
            var closes = profile.CloseApps.Where(a => a.Enabled).ToList();
            if (opens.Count + closes.Count == 0) throw new InvalidOperationException("Set up and enable at least one app first. Click an app’s Edit button to choose it on your PC.");
            foreach (var rule in opens.Concat(closes))
            {
                if (!rule.Configured) throw new InvalidOperationException($"Choose a launch target and process executable for {rule.Name}.");
                if (rule.DelaySeconds is < 0 or > 60) throw new InvalidOperationException("Launch delays must be between 0 and 60 seconds.");
                host.Validate(rule, true); // Check restart targets before closing anything.
            }
            var openPaths = opens.Select(a => Paths.Normalize(a.ProcessPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (closes.Any(a => openPaths.Contains(Paths.Normalize(a.ProcessPath))))
                throw new InvalidOperationException("An app appears in both lists. Remove it from one list before starting.");
            foreach (var group in new[] { opens, closes })
                if (group.GroupBy(a => Paths.Normalize(a.ProcessPath), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("Each executable can appear only once in a list. Edit launch arguments on that app instead.");

            var before = await host.CaptureAsync();
            Current = new SessionState
            {
                ProfileId = profile.Id, ProfileName = profile.Name,
                Baseline = before.Select(p => p.Identity).ToList()
            };
            createdSession = true;
            Save();
            Log("Info", $"Desktop snapshot saved. Starting {profile.Name}.");

            foreach (var rule in closes)
            {
                var matches = before.Where(p => Paths.Equal(p.ExecutablePath, rule.ProcessPath)).ToList();
                if (matches.Count == 0) { Log("Info", $"{rule.Name}: not running; nothing to restore later."); continue; }
                var primary = matches.OrderByDescending(p => p.HasWindow)
                    .ThenBy(p => matches.Any(other => other.Pid == p.ParentPid)).First();
                var restart = Clone(rule);
                // A shortcut can carry its own arguments. Preserve that restart route.
                if (Paths.Equal(rule.LaunchTarget, primary.ExecutablePath) && primary.Arguments is not null)
                    restart.Arguments = primary.Arguments;
                var stopped = new StoppedApp { Rule = rule, Restart = restart, OriginalProcesses = matches, CloseAttempted = true };
                Current.Stopped.Add(stopped);
                Save(); // Write intent before requesting any process to exit.
                var failed = false;
                foreach (var process in matches.OrderByDescending(p => p.HasWindow))
                {
                    try
                    {
                        var result = await host.CloseAsync(process, rule.ForceClose);
                        if (!result.Closed) { failed = true; Log("Warning", $"{rule.Name}: {result.Message}"); }
                    }
                    catch (Exception ex) { failed = true; Log("Warning", $"{rule.Name}: {ex.Message}"); }
                }
                if (!failed) Log("Success", $"Closed {rule.Name}. It will be reopened when you end the session.");
            }

            foreach (var rule in opens)
            {
                var running = await host.CaptureAsync();
                var launch = new LaunchedApp { Rule = rule, RequestedAt = DateTimeOffset.UtcNow };
                Current.Launched.Add(launch);
                if (running.Any(p => Paths.Equal(p.ExecutablePath, rule.ProcessPath)))
                {
                    launch.WasAlreadyRunning = true;
                    launch.TrackingFinished = true;
                    Log("Info", $"Kept {rule.Name}: already running. It will stay open after the session.");
                    continue;
                }
                launch.LaunchAttempted = true;
                Save();
                try
                {
                    var result = await host.LaunchAsync(rule);
                    if (result.Process is { } root && Paths.Equal(root.ExecutablePath, rule.ProcessPath) && !IsBaseline(root))
                        launch.Tracked.Add(root);
                    Save();
                    await TrackLaunchAsync(launch);
                    launch.TrackingFinished = true;
                    if (launch.Tracked.Count > 0) Log("Success", $"Opened {rule.Name}.");
                    else Log("Warning", $"{rule.Name}: launch requested, but the configured process did not appear. Check its process executable and arguments.");
                    if (rule.DelaySeconds > 0) await host.WaitAsync(TimeSpan.FromSeconds(rule.DelaySeconds));
                }
                catch (Exception ex)
                {
                    launch.TrackingFinished = true;
                    Log("Error", $"Could not open {rule.Name}: {ex.Message}");
                }
                Save();
            }
            Current.Status = "Active";
            var issues = Current.Activity.Count(a => a.Level is "Warning" or "Error");
            Log(issues > 0 ? "Warning" : "Info", issues > 0
                ? $"Session started with {issues} item(s) to check. See Session activity for details. End & restore is available."
                : "Session ready. End the session to close its apps and reopen your previous apps.");
        }
        catch
        {
            if (createdSession && Current is not null) { Current.Status = "Needs attention"; Save(); }
            throw;
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    public async Task EndAsync()
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A session operation is already in progress.");
        try
        {
            if (Current is null) return;
            Current.Status = "Restoring";
            Log("Info", "Restoring your desktop apps.");
            foreach (var launched in Current.Launched.AsEnumerable().Reverse())
            {
                if (launched.WasAlreadyRunning || !launched.LaunchAttempted || launched.CloseFinished) continue;
                var running = await host.CaptureAsync();
                if (!launched.TrackingFinished)
                {
                    // Recovery of a launch interrupted before its PID was written.
                    foreach (var p in running.Where(p => IsLaunchCandidate(p, launched))) AddTracked(launched, p);
                    launched.TrackingFinished = true;
                    Save();
                }
                var owned = FindOwned(launched, running);
                launched.Tracked = launched.Tracked.Concat(owned).DistinctBy(p => p.Identity).ToList();
                Save();
                foreach (var process in owned.OrderByDescending(p => p.HasWindow))
                {
                    try
                    {
                        var result = await host.CloseAsync(process, launched.Rule.ForceClose);
                        if (!result.Closed) Log("Warning", $"{launched.Rule.Name}: {result.Message}");
                    }
                    catch (Exception ex) { Log("Warning", $"{launched.Rule.Name}: {ex.Message}"); }
                }
                var after = await host.CaptureAsync();
                launched.CloseFinished = !FindOwned(launched, after).Any();
                Log(launched.CloseFinished ? "Success" : "Warning", launched.CloseFinished
                    ? $"{launched.Rule.Name}: session processes closed."
                    : $"{launched.Rule.Name} is still open. Save your work or exit it, then retry restore.");
            }

            foreach (var stopped in Current.Stopped)
            {
                if (!stopped.CloseAttempted || stopped.RestoreFinished) continue;
                var running = await host.CaptureAsync();
                if (running.Any(p => Paths.Equal(p.ExecutablePath, stopped.Rule.ProcessPath)))
                {
                    stopped.RestoreFinished = true;
                    Log("Info", $"{stopped.Rule.Name}: already running; kept the existing app.");
                    continue;
                }
                try
                {
                    host.Validate(stopped.Restart, true);
                    Save();
                    await host.LaunchAsync(stopped.Restart);
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        if ((await host.CaptureAsync()).Any(p => Paths.Equal(p.ExecutablePath, stopped.Rule.ProcessPath)))
                        { stopped.RestoreFinished = true; break; }
                        await host.WaitAsync(TimeSpan.FromMilliseconds(500));
                    }
                    Log(stopped.RestoreFinished ? "Success" : "Warning", stopped.RestoreFinished
                        ? $"Reopened {stopped.Rule.Name}."
                        : $"{stopped.Rule.Name}: restart requested, but its process was not detected. Check the app, then retry.");
                }
                catch (Exception ex) { Log("Error", $"Could not reopen {stopped.Rule.Name}: {ex.Message}"); }
                Save();
            }
            var pending = Current.Launched.Any(a => a.LaunchAttempted && !a.WasAlreadyRunning && !a.CloseFinished)
                || Current.Stopped.Any(a => a.CloseAttempted && !a.RestoreFinished);
            if (pending)
            {
                Current.Status = "Needs attention";
                Log("Warning", "Some apps need attention. The session record is saved; fix the items above and retry restore.");
            }
            else
            {
                Current.Status = "Completed";
                Log("Success", "Session finished. Your previous apps are back.");
                store.Complete(Current);
                Current = null;
                Changed?.Invoke();
            }
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    private async Task TrackLaunchAsync(LaunchedApp app)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var running = await host.CaptureAsync();
            foreach (var p in running.Where(p => IsLaunchCandidate(p, app))) AddTracked(app, p);
            if (app.Tracked.Count > 0) { Save(); return; }
            await host.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
    }
    private bool IsLaunchCandidate(ProcessSnapshot p, LaunchedApp app) => !IsBaseline(p)
        && Paths.Equal(p.ExecutablePath, app.Rule.ProcessPath)
        && p.StartedUtcTicks >= app.RequestedAt.UtcTicks
        && p.StartedUtcTicks <= app.RequestedAt.AddSeconds(30).UtcTicks;
    private bool IsBaseline(ProcessSnapshot p) => Current!.Baseline.Contains(p.Identity);
    private static void AddTracked(LaunchedApp app, ProcessSnapshot p)
    {
        if (!app.Tracked.Any(t => t.Identity == p.Identity)) app.Tracked.Add(p);
    }
    private List<ProcessSnapshot> FindOwned(LaunchedApp app, IReadOnlyList<ProcessSnapshot> running)
    {
        var owned = running.Where(p => !IsBaseline(p) && app.Tracked.Any(t => t.Identity == p.Identity)
            && Paths.Equal(p.ExecutablePath, app.Rule.ProcessPath)).ToList();
        // Follow this app's children, but never adopt another independently opened instance.
        bool added;
        do
        {
            added = false;
            foreach (var p in running.Where(p => !IsBaseline(p) && Paths.Equal(p.ExecutablePath, app.Rule.ProcessPath)))
                if (!owned.Any(o => o.Identity == p.Identity)
                    && owned.Any(o => o.Pid == p.ParentPid && p.StartedUtcTicks >= o.StartedUtcTicks))
                { owned.Add(p); added = true; }
        } while (added);
        return owned;
    }
    private static AppRule Clone(AppRule app) => JsonSerializer.Deserialize<AppRule>(JsonSerializer.Serialize(app, FileStore.Json), FileStore.Json)!;
    private void Save() { if (Current is not null) store.SaveActive(Current); Changed?.Invoke(); }
    private void Log(string level, string message)
    {
        Current!.Activity.Add(new(DateTimeOffset.Now, level, message));
        Save();
    }
}
