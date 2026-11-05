using System.Text.Json;
using Pitstop.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Start/end preserves existing apps and restores only previously running apps", Lifecycle),
    ("PID reuse never closes a replacement process", PidReuse),
    ("A close failure keeps the journal and retry is idempotent", CloseFailure),
    ("A restart failure can be retried without duplicate restores", RestartFailure),
    ("Saved sessions survive an engine restart", Recovery),
    ("Disabled rules have no process effects", Disabled),
    ("Conflicting lists fail before any process changes", Overlap),
    ("Missing launch targets fail before closing background apps", Preflight),
    ("An empty profile leaves the desktop untouched", Empty),
    ("A second start leaves the active session intact", DoubleStart),
    ("Independently opened later instances stay open", IndependentInstance),
    ("Owned child instances close with the session", OwnedChildren),
    ("Every process change has a durable intent record", JournalBeforeEffects),
    ("Atomic file writes keep backups and archive completed sessions", FilePersistence),
    ("A corrupt session is preserved and fails closed", CorruptSession),
    ("A failed launch still allows background apps to be restored", FailedLaunch),
    ("A self-restarted background app is not launched twice", BackgroundRestart),
    ("Force-close consent is copied into the session snapshot", ForceSnapshot),
    ("An interrupted launch is recovered using path and creation time", InterruptedLaunch),
    ("Path comparisons are exact, case-insensitive, and separator-normalized", ExactPaths),
    ("Tray apps are terminated when a window close only hides them", TrayExit),
    ("Windowless background apps are terminated without waiting for a window", BackgroundExit),
    ("Graceful-only mode never terminates a refusing app", GracefulOnly),
    ("A normal graceful exit never needs termination", NormalExit),
    ("An exited process receives no close requests", ExitedProcess),
    ("Termination is not reported successful if the process remains alive", FailedTermination),
    ("A denied termination cannot be mistaken for a successful exit", DeniedTermination),
    ("Old profiles adopt complete exit while explicit graceful-only settings persist", CloseModeMigration),
    ("Complete exit is used for background apps in both session directions", DefaultCompleteExit),
    ("Bundled helpers with different executables exit with their app", BundledHelpers),
    ("Unrelated children outside the installation folder stay open", ExternalChildren),
    ("Immediate helpers survive a bootstrapper exit and are still cleaned up", BootstrapperExit),
    ("Reused parent PIDs do not claim later unrelated children", ReusedParent),
    ("Delayed launcher processes are recovered when ending a session", LateLaunch),
    ("An unidentified launch requires manual confirmation, not false success", UnknownLaunch),
    ("A failed launch never claims a later manually opened app", FailedLaunchManualApp),
    ("A process that survives an apparent close keeps the restore record", FalseCloseSuccess),
    ("Reset abandons failed steps without any process actions", RecoveryTests.ResetFailedRestore),
    ("Reset unlocks new profiles and remains reset after restart", RecoveryTests.ResetThenStart),
    ("Reset cancels blocked inventory without waiting for retry", RecoveryTests.ResetDuringInventory),
    ("Reset stops after an issued close without reopening other apps", RecoveryTests.ResetDuringClose),
    ("Reset cancels long launch delays and prevents the next launch", RecoveryTests.ResetDuringDelay),
    ("Stop without reset keeps a resumable record", RecoveryTests.StopAndResume),
    ("Interrupted terminal resets are not resurrected", RecoveryTests.TerminalResetRecovery),
    ("Archive failure preserves a reset record without process actions", RecoveryTests.ResetArchiveFailure),
    ("Profile JSON and order survive reset and app-folder replacement", RecoveryTests.ProfilesSurviveUpdateAndReset),
    ("Missing profiles recover the latest saved JSON", RecoveryTests.MissingProfileRecovery),
    ("Damaged profiles recover without discarding the original", RecoveryTests.DamagedProfileRecovery),
    ("Legacy backups recover before defaults are created", RecoveryTests.LegacyProfileBackup),
    ("Unrecoverable profiles are never overwritten by starters", RecoveryTests.UnrecoverableProfiles),
    ("An intentionally empty profile list stays empty", RecoveryTests.EmptyProfilesPersist),
    ("Backup failure never prevents primary profile persistence", RecoveryTests.BackupFailure)
};
int failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}
Console.WriteLine($"\n{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static AppRule Rule(string name, bool enabled = true) => new()
{ Name = name, LaunchTarget = $@"C:\Apps\{name}.exe", ProcessPath = $@"C:\Apps\{name}.exe", DelaySeconds = 0, Enabled = enabled };
static Profile Profile(params AppRule[] open) => new() { Name = "Test race", OpenApps = open.ToList() };
static (SessionEngine Engine, FakeHost Host, MemoryStore Store) Setup()
{
    var host = new FakeHost(); var store = new MemoryStore();
    return (new(host, store), host, store);
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Throws(Func<Task> action)
{ try { await action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected an InvalidOperationException"); }

static async Task Lifecycle()
{
    var (engine, host, store) = Setup();
    var helper = Rule("Helper"); var game = Rule("Game"); var plex = Rule("Plex"); var notes = Rule("Notes");
    var originalHelper = host.Add(helper); host.Add(plex, "--autostart \"keep quoted arguments\"");
    var profile = Profile(helper, game); profile.CloseApps = [plex, notes];
    await engine.StartAsync(profile);
    Check(host.Running.Any(p => p.Identity == originalHelper.Identity), "Existing helper was closed");
    Check(!host.Running.Any(p => p.ExecutablePath == plex.ProcessPath), "Plex was not closed");
    Check(host.Starts.Count == 1 && host.Starts[0].Name == "Game", "Already-running app was duplicated");
    await engine.EndAsync();
    Check(engine.Current is null && store.Completed.Count == 1, "Session did not finish");
    Check(host.Running.Any(p => p.Identity == originalHelper.Identity), "Existing helper did not survive end");
    Check(host.Running.All(p => p.ExecutablePath != game.ProcessPath), "Session game stayed open");
    Check(host.Starts.Count(a => a.Name == "Plex") == 1 && !host.Starts.Any(a => a.Name == "Notes"), "Restore set was wrong");
    Check(host.Starts.Single(a => a.Name == "Plex").Arguments == "--autostart \"keep quoted arguments\"", "Original arguments were lost");
}
static async Task PidReuse()
{
    var (engine, host, _) = Setup(); var game = Rule("Game");
    await engine.StartAsync(Profile(game));
    var old = host.Running.Single(); host.Running.Clear();
    var replacement = host.Add(game); replacement.Pid = old.Pid; replacement.StartedUtcTicks = old.StartedUtcTicks + 1000;
    await engine.EndAsync();
    Check(host.Running.Contains(replacement) && host.Closes.Count == 0, "Reused PID was targeted");
}
static async Task CloseFailure()
{
    var (engine, host, store) = Setup(); var game = Rule("Game"); var plex = Rule("Plex"); host.Add(plex);
    var profile = Profile(game); profile.CloseApps = [plex]; await engine.StartAsync(profile);
    host.DenyClose.Add(game.ProcessPath); await engine.EndAsync();
    Check(engine.Current?.Status == "Needs attention" && store.Active is not null, "Failure discarded the recovery record");
    Check(host.Starts.Count(a => a.Name == "Plex") == 1, "Other apps were not restored after a close failure");
    host.DenyClose.Clear(); await engine.EndAsync();
    Check(engine.Current is null && host.Starts.Count(a => a.Name == "Plex") == 1, "Retry duplicated a restored app");
}
static async Task RestartFailure()
{
    var (engine, host, _) = Setup(); var game = Rule("Game"); var plex = Rule("Plex"); host.Add(plex);
    var profile = Profile(game); profile.CloseApps = [plex]; await engine.StartAsync(profile);
    host.DenyLaunch.Add(plex.ProcessPath); await engine.EndAsync();
    Check(engine.Current?.Stopped.Single().RestoreFinished == false, "Failed restore was marked complete");
    host.DenyLaunch.Clear(); await engine.EndAsync();
    Check(engine.Current is null && host.Starts.Count(a => a.Name == "Plex") == 1, "Retry did not restore Plex exactly once");
}
static async Task Recovery()
{
    var (engine, host, store) = Setup(); var game = Rule("Game"); var plex = Rule("Plex"); host.Add(plex);
    var profile = Profile(game); profile.CloseApps = [plex]; await engine.StartAsync(profile);
    var recovered = new SessionEngine(host, store); await recovered.EndAsync();
    Check(recovered.Current is null && host.Running.Single().ExecutablePath == plex.ProcessPath, "Recovery did not restore desktop");
}
static async Task Disabled()
{
    var (engine, host, _) = Setup(); var game = Rule("Game"); var disabled = Rule("Disabled", false); host.Add(disabled);
    var p = Profile(game, disabled); p.CloseApps = [Rule("Disabled", false)]; await engine.StartAsync(p); await engine.EndAsync();
    Check(!host.Starts.Any(a => a.Name == "Disabled") && host.Running.Single().ExecutablePath == disabled.ProcessPath, "Disabled app was changed");
}
static async Task Overlap()
{
    var (engine, host, _) = Setup(); var p = Profile(Rule("Game")); p.CloseApps = [Rule("Game")];
    await Throws(() => engine.StartAsync(p)); Check(engine.Current is null && host.Effects == 0, "Overlap changed processes");
}
static async Task Preflight()
{
    var (engine, host, _) = Setup(); var p = Profile(Rule("Missing")); p.CloseApps = [Rule("Plex")]; host.Add(p.CloseApps[0]);
    host.Invalid.Add(p.OpenApps[0].ProcessPath); await Throws(() => engine.StartAsync(p));
    Check(host.Effects == 0 && engine.Current is null, "Preflight failure closed an app");
}
static async Task Empty()
{
    var (engine, host, _) = Setup(); await Throws(() => engine.StartAsync(Profile())); Check(host.Effects == 0 && engine.Current is null, "Empty profile created a session");
}
static async Task DoubleStart()
{
    var (engine, host, _) = Setup(); await engine.StartAsync(Profile(Rule("Game"))); var id = engine.Current!.Id;
    await Throws(() => engine.StartAsync(Profile(Rule("Other"))));
    Check(engine.Current!.Id == id && engine.Current.Status == "Active" && host.Starts.Count == 1, "Second start modified the active session");
}
static async Task IndependentInstance()
{
    var (engine, host, _) = Setup(); var game = Rule("Game"); await engine.StartAsync(Profile(game)); var manual = host.Add(game);
    await engine.EndAsync(); Check(host.Running.Single().Identity == manual.Identity, "A manually opened later instance was closed");
}
static async Task OwnedChildren()
{
    var (engine, host, _) = Setup(); var game = Rule("Game"); await engine.StartAsync(Profile(game));
    var root = host.Running.Single(); var child = host.Add(game); child.ParentPid = root.Pid;
    await engine.EndAsync(); Check(host.Running.Count == 0 && host.Closes.Count == 2, "Owned child remained open");
}
static async Task JournalBeforeEffects()
{
    var (engine, host, store) = Setup(); var game = Rule("Game"); var plex = Rule("Plex"); host.Add(plex);
    host.BeforeClose = p => Check(store.Active?.Stopped.Any(s => s.CloseAttempted && s.OriginalProcesses.Any(o => o.Identity == p.Identity)) == true, "Close had no durable intent");
    host.BeforeLaunch = app => Check(store.Active?.Launched.Any(a => a.LaunchAttempted && a.Rule.Id == app.Id) == true, "Launch had no durable intent");
    var profile = Profile(game); profile.CloseApps = [plex]; await engine.StartAsync(profile);
    Check(host.Starts.Count == 1 && host.Closes.Count == 1, "Expected effects were missing");
}
static Task FilePersistence()
{
    var folder = Path.Combine(Environment.CurrentDirectory, "work", "test-data", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new FileStore(folder); var p = store.LoadProfiles();
        Check(p.Count == 3 && p.SelectMany(x => x.OpenApps.Concat(x.CloseApps)).All(a => !a.Enabled), "Starter apps should be off");
        p[0].Name = "Changed"; store.SaveProfiles(p); Check(store.LoadProfiles()[0].Name == "Changed", "Profile save failed");
        var state = new SessionState(); store.SaveActive(state); state.Status = "Active"; store.SaveActive(state);
        Check(File.Exists(Path.Combine(folder, "active-session.json.bak")), "No journal backup");
        Check(store.LoadActive()!.Status == "Active", "Journal did not reload");
        state.Status = "Completed"; store.SaveActive(state); store.Complete(state);
        Check(store.LoadActive() is null && Directory.GetFiles(Path.Combine(folder, "history")).Length == 1, "Archive did not complete");
    }
    finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    return Task.CompletedTask;
}
static Task CorruptSession()
{
    var folder = Path.Combine(Environment.CurrentDirectory, "work", "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    try
    {
        var path = Path.Combine(folder, "active-session.json"); File.WriteAllText(path, "{ interrupted");
        try { new FileStore(folder).LoadActive(); throw new Exception("Corrupt journal was accepted"); }
        catch (InvalidDataException) { Check(File.ReadAllText(path) == "{ interrupted", "Corrupt file was overwritten"); }
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
}
static async Task FailedLaunch()
{
    var (engine, host, _) = Setup(); var game = Rule("Game"); var plex = Rule("Plex"); host.Add(plex); host.DenyLaunch.Add(game.ProcessPath);
    var profile = Profile(game); profile.CloseApps = [plex]; await engine.StartAsync(profile); await engine.EndAsync();
    Check(engine.Current is null && host.Running.Single().ExecutablePath == plex.ProcessPath, "Launch failure stranded background apps");
}
static async Task BackgroundRestart()
{
    var (engine, host, _) = Setup(); var plex = Rule("Plex"); host.Add(plex); var p = Profile(); p.CloseApps = [plex];
    await engine.StartAsync(p); var restarted = host.Add(plex); await engine.EndAsync();
    Check(host.Starts.Count == 0 && host.Running.Single().Identity == restarted.Identity, "Self-restarted app duplicated");
}
static async Task ForceSnapshot()
{
    var (engine, host, _) = Setup(); var game = Rule("Game"); game.ForceClose = true; await engine.StartAsync(Profile(game));
    game.ForceClose = false; await engine.EndAsync(); Check(host.CloseFlags.Single(), "Session consent changed after profile mutation");
}
static async Task InterruptedLaunch()
{
    var host = new FakeHost(); var game = Rule("Game"); var request = DateTimeOffset.UtcNow.AddSeconds(-1); var process = host.Add(game);
    var store = new MemoryStore { Active = new SessionState { Launched = [new LaunchedApp { Rule = game, RequestedAt = request, LaunchAttempted = true }] } };
    var recovered = new SessionEngine(host, store); await recovered.EndAsync();
    Check(host.Running.Count == 0 && recovered.Current is null, "Interrupted launch did not recover");
}
static Task ExactPaths()
{
    Check(Paths.Equal(@"C:\APPS\Game.exe", "c:/apps/game.exe"), "Case or slash comparison failed");
    Check(!Paths.Equal(@"C:\Apps\Game.exe", @"C:\Other\Game.exe"), "Different executables matched by name");
    return Task.CompletedTask;
}

static async Task TrayExit()
{
    var app = new FakeLifetime { HasWindow = true };
    var result = await ProcessExit.CloseAsync(app, true);
    Check(result.Closed && !app.IsRunning && app.Terminations == 1 && app.CloseRequests == 1, "Tray process was left alive");
}
static async Task BackgroundExit()
{
    var app = new FakeLifetime(); var result = await ProcessExit.CloseAsync(app, true);
    Check(result.Closed && app.Terminations == 1 && app.Waits == 1, "Background process did not exit immediately");
}
static async Task GracefulOnly()
{
    var app = new FakeLifetime { HasWindow = true }; var result = await ProcessExit.CloseAsync(app, false);
    Check(!result.Closed && app.IsRunning && app.Terminations == 0, "Graceful-only mode terminated an app");
}
static async Task NormalExit()
{
    var app = new FakeLifetime { HasWindow = true, ExitsGracefully = true }; var result = await ProcessExit.CloseAsync(app, true);
    Check(result.Closed && app.Terminations == 0, "A normal exit was forced");
}
static async Task ExitedProcess()
{
    var app = new FakeLifetime { IsRunning = false }; var result = await ProcessExit.CloseAsync(app, true);
    Check(result.Closed && app.CloseRequests == 0 && app.Terminations == 0, "Exited process was touched");
}
static async Task FailedTermination()
{
    var app = new FakeLifetime { RefusesTermination = true }; var result = await ProcessExit.CloseAsync(app, true);
    Check(!result.Closed && app.IsRunning, "A live process was reported closed");
}
static async Task DeniedTermination()
{
    var app = new FakeLifetime { DenyTermination = true };
    await Throws(async () => await ProcessExit.CloseAsync(app, true));
    Check(app.IsRunning, "Denied app changed state");
}
static Task CloseModeMigration()
{
    var old = JsonSerializer.Deserialize<AppRule>("{\"Name\":\"Trading Paints\",\"ForceClose\":false}", FileStore.Json)!;
    Check(old.ForceClose && old.IncludeChildProcesses, "Old default still leaves tray apps alive");
    old.CloseMode = AppCloseMode.GracefulOnly;
    var saved = JsonSerializer.Deserialize<AppRule>(JsonSerializer.Serialize(old, FileStore.Json), FileStore.Json)!;
    Check(!saved.ForceClose && saved.CloseMode == AppCloseMode.GracefulOnly, "Explicit graceful-only choice did not persist");
    return Task.CompletedTask;
}
static async Task DefaultCompleteExit()
{
    var (engine, host, _) = Setup(); var game = Rule("iRacingUI"); var plex = Rule("Plex"); host.Add(plex);
    var profile = Profile(game); profile.CloseApps = [plex]; await engine.StartAsync(profile); await engine.EndAsync();
    Check(host.CloseFlags.Count == 2 && host.CloseFlags.All(f => f), "Some default closes only requested a window close");
}
static async Task BundledHelpers()
{
    var (engine, host, _) = Setup(); var app = Rule("iRacingUI"); await engine.StartAsync(Profile(app));
    var root = host.Running.Single(); var helper = host.Add(Rule("resources\\Helper")); helper.ParentPid = root.Pid;
    await engine.EndAsync(); Check(host.Running.Count == 0, "Different-executable helper was left running");
}
static async Task ExternalChildren()
{
    var (engine, host, _) = Setup(); var app = Rule("iRacingUI"); await engine.StartAsync(Profile(app));
    var root = host.Running.Single(); var rule = Rule("Browser"); rule.ProcessPath = @"C:\Browser\browser.exe";
    var browser = host.Add(rule); browser.ParentPid = root.Pid;
    await engine.EndAsync(); Check(host.Running.Single().Identity == browser.Identity, "External browser was adopted as a bundled helper");
}
static async Task BootstrapperExit()
{
    var (engine, host, _) = Setup(); var app = Rule("Bootstrap"); await engine.StartAsync(Profile(app));
    var root = host.Running.Single(); host.Running.Clear(); var child = host.Add(Rule("resources\\Worker")); child.ParentPid = root.Pid;
    await engine.EndAsync(); Check(host.Running.Count == 0, "Orphaned bootstrapper helper was missed");
}
static async Task ReusedParent()
{
    var (engine, host, _) = Setup(); var app = Rule("App"); await engine.StartAsync(Profile(app));
    var root = host.Running.Single(); host.Running.Clear(); var replacement = host.Add(app); replacement.Pid = root.Pid;
    replacement.StartedUtcTicks = root.StartedUtcTicks + 1000;
    var child = host.Add(Rule("Helper")); child.ParentPid = root.Pid; child.StartedUtcTicks = replacement.StartedUtcTicks + 1000;
    await engine.EndAsync(); Check(host.Running.Count == 2, "Reused parent PID claimed another process family");
}
static async Task LateLaunch()
{
    var (engine, host, _) = Setup(); host.SkipLaunchDetection = true; var app = Rule("SlowLauncher");
    await engine.StartAsync(Profile(app)); engine.Current!.Launched.Single().RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
    host.Add(app); await engine.EndAsync();
    Check(host.Running.Count == 0 && engine.Current is null, "Delayed launcher was silently missed");
}
static async Task UnknownLaunch()
{
    var (engine, host, store) = Setup(); host.SkipLaunchDetection = true; await engine.StartAsync(Profile(Rule("Unknown")));
    await engine.EndAsync(); Check(engine.Current?.Status == "Needs attention", "Unidentified app was falsely reported closed");
    await engine.ConfirmUntrackedAppsClosedAsync(); Check(engine.Current is null && store.Completed.Count == 1, "Manual resolution did not finish restore");
}
static async Task FailedLaunchManualApp()
{
    var (engine, host, _) = Setup(); var app = Rule("App"); host.DenyLaunch.Add(app.ProcessPath);
    await engine.StartAsync(Profile(app)); var manual = host.Add(app); await engine.EndAsync();
    Check(host.Running.Single().Identity == manual.Identity, "Failed launch adopted an unrelated manual instance");
}
static async Task FalseCloseSuccess()
{
    var (engine, host, _) = Setup(); var app = Rule("Tray"); await engine.StartAsync(Profile(app));
    host.FalseCloseSuccess = true; await engine.EndAsync();
    Check(engine.Current?.Status == "Needs attention" && host.Running.Count == 1, "A surviving process was reported closed");
}

sealed class MemoryStore : ISessionStore
{
    public bool FailComplete;
    public SessionState? Active;
    public List<SessionState> Completed = [];
    private static SessionState Copy(SessionState s) => JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(s, FileStore.Json), FileStore.Json)!;
    public SessionState? LoadActive() => Active is null ? null : Copy(Active);
    public void SaveActive(SessionState session) => Active = Copy(session);
    public void Complete(SessionState session) { if (FailComplete) throw new IOException("Archive write failed"); Completed.Add(Copy(session)); Active = null; }
}
sealed class FakeHost : IProcessHost
{
    public int Captures;
    public TaskCompletionSource<IReadOnlyList<ProcessSnapshot>>? CaptureBlocker;
    public TaskCompletionSource? DelayBlocker;
    public bool SkipLaunchDetection;
    public bool FalseCloseSuccess;
    public List<ProcessSnapshot> Running = [];
    public List<AppRule> Starts = [];
    public List<ProcessSnapshot> Closes = [];
    public List<bool> CloseFlags = [];
    public HashSet<string> DenyClose = [];
    public HashSet<string> DenyLaunch = [];
    public HashSet<string> Invalid = [];
    public Action<ProcessSnapshot>? BeforeClose;
    public Action<AppRule>? BeforeLaunch;
    private int nextPid = 100;
    public int Effects => Starts.Count + Closes.Count;
    public ProcessSnapshot Add(AppRule rule, string args = "")
    {
        var process = new ProcessSnapshot { Pid = nextPid++, StartedUtcTicks = DateTime.UtcNow.Ticks, ExecutablePath = rule.ProcessPath, Arguments = args };
        Running.Add(process); return process;
    }
    public Task<IReadOnlyList<ProcessSnapshot>> CaptureAsync() { Captures++; return CaptureBlocker?.Task ?? Task.FromResult<IReadOnlyList<ProcessSnapshot>>(Running.ToList()); }
    public void Validate(AppRule rule, bool forLaunch) { if (Invalid.Contains(rule.ProcessPath)) throw new InvalidOperationException("Missing target"); }
    public Task<LaunchResult> LaunchAsync(AppRule rule)
    {
        BeforeLaunch?.Invoke(rule);
        if (DenyLaunch.Contains(rule.ProcessPath)) throw new InvalidOperationException("Launch denied");
        if (SkipLaunchDetection) return Task.FromResult(new LaunchResult(null));
        Starts.Add(rule); return Task.FromResult(new LaunchResult(Add(rule, rule.Arguments)));
    }
    public Task<CloseResult> CloseAsync(ProcessSnapshot process, bool force)
    {
        BeforeClose?.Invoke(process); Closes.Add(process); CloseFlags.Add(force);
        if (DenyClose.Contains(process.ExecutablePath)) return Task.FromResult(new CloseResult(false, "Denied by fake host"));
        if (!FalseCloseSuccess) Running.RemoveAll(p => p.Identity == process.Identity);
        return Task.FromResult(new CloseResult(true, "Closed"));
    }
    public Task WaitAsync(TimeSpan delay) => DelayBlocker?.Task ?? Task.CompletedTask;
}
sealed class FakeLifetime : IProcessLifetime
{
    public bool IsRunning { get; set; } = true;
    public bool HasWindow, ExitsGracefully, RefusesTermination, DenyTermination;
    public int CloseRequests, Terminations, Waits;
    public bool RequestWindowClose() { CloseRequests++; return HasWindow; }
    public Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        Waits++;
        if (ExitsGracefully || (Terminations > 0 && !RefusesTermination)) IsRunning = false;
        return Task.FromResult(!IsRunning);
    }
    public void Terminate()
    {
        if (DenyTermination) throw new InvalidOperationException("Windows denied access");
        Terminations++;
    }
}
