using System.Text.Json;

namespace Pitstop.Core;

public sealed class SessionEngine
{
    private readonly IProcessHost host;
    private readonly ISessionStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? operationCancellation;
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
        using var operation = new CancellationTokenSource();
        operationCancellation = operation;
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

            var before = await CaptureAsync();
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
                var roots = before.Where(p => Paths.Equal(p.ExecutablePath, rule.ProcessPath)).ToList();
                if (roots.Count == 0) { Log("Info", $"{rule.Name}: no process matches {rule.ProcessPath}. Nothing was closed. If the app is running, use Pick a running app to correct its process path."); continue; }
                var matches = ProcessFamily.Find(rule, roots, before);
                var primary = roots.OrderByDescending(p => p.HasWindow)
                    .ThenBy(p => roots.Any(other => other.Pid == p.ParentPid)).First();
                var restart = Clone(rule);
                // A shortcut can carry its own arguments. Preserve that restart route.
                if (Paths.Equal(rule.LaunchTarget, primary.ExecutablePath) && primary.Arguments is not null)
                    restart.Arguments = primary.Arguments;
                var stopped = new StoppedApp { Rule = rule, Restart = restart, OriginalProcesses = matches, CloseAttempted = true };
                Current.Stopped.Add(stopped);
                Save(); // Write intent before requesting any process to exit.
                var verified = await CloseGroupAsync(rule, matches);
                var afterClose = await CaptureAsync();
                var survivors = ProcessFamily.Find(rule, matches, afterClose);
                // A program can spawn a helper while handling its window close.
                var newHelpers = survivors.Where(p => !matches.Any(m => m.Identity == p.Identity)).ToList();
                if (newHelpers.Count > 0)
                {
                    stopped.OriginalProcesses.AddRange(newHelpers); Save();
                    verified &= await CloseGroupAsync(rule, newHelpers);
                    afterClose = await CaptureAsync();
                    survivors = ProcessFamily.Find(rule, stopped.OriginalProcesses, afterClose);
                }
                if (survivors.Count == 0 && verified) Log("Success", $"Exited {rule.Name} and its selected helpers. It will be reopened when you end the session.");
                else Log("Warning", $"{rule.Name}: complete exit could not be verified. Check the exit result in Session activity.");
            }

            foreach (var rule in opens)
            {
                var running = await CaptureAsync();
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
                var launchReturned = false;
                try
                {
                    CheckStop();
                    var result = await host.LaunchAsync(rule);
                    launchReturned = true;
                    if (result.Process is { } root && Paths.Equal(root.ExecutablePath, rule.ProcessPath) && !IsBaseline(root))
                        launch.Tracked.Add(root);
                    Save();
                    await TrackLaunchAsync(launch);
                    launch.TrackingFinished = true;
                    if (launch.Tracked.Count > 0) Log("Success", $"Opened {rule.Name}.");
                    else Log("Warning", $"{rule.Name}: launch requested, but the configured process did not appear. Check its process executable and arguments.");
                    if (rule.DelaySeconds > 0) await DelayAsync(TimeSpan.FromSeconds(rule.DelaySeconds));
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    launch.LaunchFailed = !launchReturned;
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
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            PauseRecord();
        }
        catch
        {
            if (createdSession && Current is not null) { Current.Status = "Needs attention"; Save(); }
            throw;
        }
        finally { operationCancellation = null; gate.Release(); Changed?.Invoke(); }
    }

    public async Task EndAsync()
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A session operation is already in progress.");
        using var operation = new CancellationTokenSource();
        operationCancellation = operation;
        try
        {
            if (Current is null) return;
            Current.Status = "Restoring";
            Log("Info", "Restoring your desktop apps.");
            foreach (var launched in Current.Launched.AsEnumerable().Reverse())
            {
                if (launched.WasAlreadyRunning || !launched.LaunchAttempted || launched.CloseFinished) continue;
                var running = await CaptureAsync();
                if (!launched.TrackingFinished || (launched.Tracked.Count == 0 && !launched.LaunchFailed))
                {
                    // Recovery of a launch interrupted before its PID was written.
                    foreach (var p in running.Where(p => !IsBaseline(p) && Paths.Equal(p.ExecutablePath, launched.Rule.ProcessPath)
                        && p.StartedUtcTicks >= launched.RequestedAt.UtcTicks)) AddTracked(launched, p);
                    launched.TrackingFinished = true;
                    Save();
                }
                var owned = FindOwned(launched, running);
                launched.Tracked = launched.Tracked.Concat(owned).DistinctBy(p => p.Identity).ToList();
                Save();
                var attempts = launched.Tracked.Where(p => !running.Any(current => current.Pid == p.Pid && current.Identity != p.Identity)).ToList();
                var verified = await CloseGroupAsync(launched.Rule, attempts);
                var after = await CaptureAsync();
                var survivors = FindOwned(launched, after);
                var newHelpers = survivors.Where(p => !launched.Tracked.Any(t => t.Identity == p.Identity)).ToList();
                if (newHelpers.Count > 0)
                {
                    launched.Tracked.AddRange(newHelpers); Save();
                    verified &= await CloseGroupAsync(launched.Rule, newHelpers);
                    after = await CaptureAsync();
                    survivors = FindOwned(launched, after);
                }
                // An undetected launch is not proof of successful closure.
                var untracked = launched.Tracked.Count == 0 && !launched.LaunchFailed;
                launched.CloseFinished = survivors.Count == 0 && !untracked && verified;
                Log(launched.CloseFinished ? "Success" : "Warning", launched.CloseFinished
                    ? $"{launched.Rule.Name}: session processes closed."
                    : untracked ? $"{launched.Rule.Name}: the launched process could not be identified. Check its process path and exit it manually if needed."
                    : $"{launched.Rule.Name}: {survivors.Count} process(es) are still running. Check permissions or exit it manually, then retry restore.");
            }

            foreach (var stopped in Current.Stopped)
            {
                if (!stopped.CloseAttempted || stopped.RestoreFinished) continue;
                var running = await CaptureAsync();
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
                    CheckStop();
                    await host.LaunchAsync(stopped.Restart);
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        if ((await CaptureAsync()).Any(p => Paths.Equal(p.ExecutablePath, stopped.Rule.ProcessPath)))
                        { stopped.RestoreFinished = true; break; }
                        await DelayAsync(TimeSpan.FromMilliseconds(500));
                    }
                    Log(stopped.RestoreFinished ? "Success" : "Warning", stopped.RestoreFinished
                        ? $"Reopened {stopped.Rule.Name}."
                        : $"{stopped.Rule.Name}: restart requested, but its process was not detected. Check the app, then retry.");
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log("Error", $"Could not reopen {stopped.Rule.Name}: {ex.Message}"); }
                Save();
            }
            var pending = Current.Launched.Any(a => a.LaunchAttempted && !a.WasAlreadyRunning && !a.CloseFinished)
                || Current.Stopped.Any(a => a.CloseAttempted && !a.RestoreFinished);
            if (pending)
            {
                Current.Status = "Needs attention";
                Log("Warning", "Some apps need attention. Retry restore, reset the session, or exit while keeping the saved record.");
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
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { PauseRecord(); }
        catch
        {
            if (Current is not null) { Current.Status = "Needs attention"; Save(); }
            throw;
        }
        finally { operationCancellation = null; gate.Release(); Changed?.Invoke(); }
    }

    public void RequestStop()
    {
        try { operationCancellation?.Cancel(); }
        catch (ObjectDisposedException) { } // The operation just completed.
    }
    public async Task ResetAsync()
    {
        RequestStop();
        // Wait for an already-issued process action before clearing its record.
        // Reset itself never launches, closes, or inventories apps.
        await gate.WaitAsync();
        try
        {
            if (Current is null) return;
            Current.Status = "Abandoned";
            Log("Warning", "Session reset by user. Remaining restore steps were abandoned; apps were left as they are. Profiles were kept.");
            store.Complete(Current);
            Current = null;
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    private void PauseRecord()
    {
        if (Current is null) return;
        Current.Status = "Paused";
        Log("Info", "Session actions stopped. Resume restore, reset the session, or exit. Profiles are kept.");
    }
    private CancellationToken StopToken => operationCancellation?.Token ?? CancellationToken.None;
    private void CheckStop() => StopToken.ThrowIfCancellationRequested();
    private async Task<IReadOnlyList<ProcessSnapshot>> CaptureAsync()
    {
        CheckStop();
        var result = await host.CaptureAsync().WaitAsync(StopToken);
        CheckStop(); return result;
    }
    private async Task DelayAsync(TimeSpan delay)
    {
        CheckStop();
        await host.WaitAsync(delay).WaitAsync(StopToken);
        CheckStop();
    }

    private async Task TrackLaunchAsync(LaunchedApp app)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var running = await CaptureAsync();
            foreach (var p in running.Where(p => IsLaunchCandidate(p, app))) AddTracked(app, p);
            if (app.Tracked.Count > 0) { Save(); return; }
            await DelayAsync(TimeSpan.FromMilliseconds(500));
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
        return ProcessFamily.Find(app.Rule, app.Tracked, running, Current!.Baseline);
    }
    public async Task ConfirmUntrackedAppsClosedAsync()
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A session operation is already in progress.");
        try
        {
            if (Current is null) return;
            foreach (var app in Current.Launched.Where(a => a.LaunchAttempted && !a.WasAlreadyRunning && a.Tracked.Count == 0))
                app.CloseFinished = true;
            Log("Info", "User confirmed that the unidentified apps have been exited manually.");
        }
        finally { gate.Release(); }
        await EndAsync();
    }
    private async Task<bool> CloseGroupAsync(AppRule rule, IReadOnlyList<ProcessSnapshot> processes)
    {
        async Task<(ProcessSnapshot Process, CloseResult Result)> Close(ProcessSnapshot p)
        {
            try { CheckStop(); return (p, await host.CloseAsync(p, rule.ForceClose)); }
            catch (OperationCanceledException) when (StopToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (p, new(false, ex.Message)); }
        }
        var results = await Task.WhenAll(processes.Select(Close));
        foreach (var (p, result) in results)
            Log(result.Closed ? "Info" : "Warning", $"{rule.Name} · PID {p.Pid}: {result.Message}");
        CheckStop();
        return results.All(r => r.Result.Closed);
    }
    private static AppRule Clone(AppRule app) => JsonSerializer.Deserialize<AppRule>(JsonSerializer.Serialize(app, FileStore.Json), FileStore.Json)!;
    private void Save() { if (Current is not null) store.SaveActive(Current); Changed?.Invoke(); }
    private void Log(string level, string message)
    {
        Current!.Activity.Add(new(DateTimeOffset.Now, level, message));
        Save();
    }
}
