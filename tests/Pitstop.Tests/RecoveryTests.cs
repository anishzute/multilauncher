using System.Text.Json;
using Pitstop.Core;

internal static class RecoveryTests
{
    private static AppRule Rule(string name) => new() { Name = name, LaunchTarget = $@"C:\Apps\{name}.exe", ProcessPath = $@"C:\Apps\{name}.exe", DelaySeconds = 0 };
    private static Profile Profile(params AppRule[] rules) => new() { Name = "Custom profile", OpenApps = rules.ToList() };
    private static (SessionEngine Engine, FakeHost Host, MemoryStore Store) Setup()
    { var host = new FakeHost(); var store = new MemoryStore(); return (new(host, store), host, store); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string TestFolder()
    {
        var folder = Path.Combine(Environment.CurrentDirectory, "work", "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }
    public static async Task ResetFailedRestore()
    {
        var (engine, host, store) = Setup(); var game = Rule("Game"); var plex = Rule("Plex"); host.Add(plex);
        var profile = Profile(game); profile.CloseApps = [plex]; await engine.StartAsync(profile);
        host.DenyClose.Add(game.ProcessPath); host.DenyLaunch.Add(plex.ProcessPath); await engine.EndAsync();
        var effects = host.Effects; var captures = host.Captures;
        await engine.ResetAsync();
        Check(engine.Current is null && store.Active is null && store.Completed.Single().Status == "Abandoned", "Reset did not abandon the record");
        Check(host.Effects == effects && host.Captures == captures, "Reset attempted another restore or process action");
        Check(host.Running.Any(p => p.ExecutablePath == game.ProcessPath), "Reset should leave running apps alone");
    }
    public static async Task ResetThenStart()
    {
        var (engine, host, store) = Setup(); await engine.StartAsync(Profile(Rule("Old"))); await engine.ResetAsync();
        var restarted = new SessionEngine(host, store); Check(restarted.Current is null, "Reset reappeared after restart");
        await restarted.StartAsync(Profile(Rule("New")));
        Check(restarted.Current?.Status == "Active" && host.Running.Count == 2, "Reset did not unlock the next profile or touched the previous app");
    }
    public static async Task ResetDuringInventory()
    {
        var (engine, host, store) = Setup(); await engine.StartAsync(Profile(Rule("Game")));
        host.CaptureBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = host.Effects; var restore = engine.EndAsync(); var reset = engine.ResetAsync();
        await Task.WhenAll(restore, reset).WaitAsync(TimeSpan.FromSeconds(3));
        host.CaptureBlocker.SetResult(host.Running.ToList());
        Check(engine.Current is null && store.Active is null && host.Effects == effects, "Blocked inventory prevented reset or continued restoration");
    }
    public static async Task ResetDuringClose()
    {
        var (engine, host, _) = Setup(); var game = Rule("Game"); var plex = Rule("Plex"); host.Add(plex);
        var p = Profile(game); p.CloseApps = [plex]; await engine.StartAsync(p);
        Task? reset = null; host.BeforeClose = _ => reset = engine.ResetAsync();
        await engine.EndAsync(); await reset!.WaitAsync(TimeSpan.FromSeconds(3));
        Check(engine.Current is null && host.Starts.Count == 1, "Restore reopened Plex after reset was requested");
    }
    public static async Task ResetDuringDelay()
    {
        var (engine, host, _) = Setup(); var first = Rule("First"); first.DelaySeconds = 60;
        host.DelayBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = engine.StartAsync(Profile(first, Rule("Second"))); var reset = engine.ResetAsync();
        await Task.WhenAll(start, reset).WaitAsync(TimeSpan.FromSeconds(3));
        host.DelayBlocker.SetResult();
        Check(engine.Current is null && host.Starts.Count == 1 && host.Starts[0].Name == "First", "Delay was not canceled or the next app was launched");
    }
    public static async Task StopAndResume()
    {
        var (engine, host, store) = Setup(); await engine.StartAsync(Profile(Rule("Game")));
        host.CaptureBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = engine.EndAsync(); engine.RequestStop(); await ending.WaitAsync(TimeSpan.FromSeconds(3));
        Check(engine.Current?.Status == "Paused" && store.Active?.Status == "Paused", "Stop did not retain a paused record");
        host.CaptureBlocker.SetResult(host.Running.ToList()); host.CaptureBlocker = null;
        var resumed = new SessionEngine(host, store); await resumed.EndAsync();
        Check(resumed.Current is null && host.Running.Count == 0, "Paused session could not be resumed");
    }
    public static Task TerminalResetRecovery()
    {
        var folder = TestFolder();
        try
        {
            var store = new FileStore(folder); store.SaveActive(new SessionState { Status = "Abandoned" });
            var host = new FakeHost(); var engine = new SessionEngine(host, store);
            Check(engine.Current is null && host.Effects == 0 && !File.Exists(Path.Combine(folder, "active-session.json")), "Terminal reset came back as active");
            Check(FileStore.Read<SessionState>(Directory.GetFiles(Path.Combine(folder, "history"), "*.json").Single()).Status == "Abandoned", "Reset archive was lost");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    public static async Task ResetArchiveFailure()
    {
        var (engine, host, store) = Setup(); await engine.StartAsync(Profile(Rule("Game"))); var effects = host.Effects;
        store.FailComplete = true;
        try { await engine.ResetAsync(); throw new Exception("Expected archive failure"); } catch (IOException) { }
        Check(engine.Current is not null && store.Active?.Status == "Abandoned", "Archive failure destroyed the record");
        store.FailComplete = false; await engine.ResetAsync();
        Check(engine.Current is null && host.Effects == effects, "Reset retry repeated a process action");
    }
    public static async Task ProfilesSurviveUpdateAndReset()
    {
        var folder = TestFolder();
        try
        {
            var data = Path.Combine(folder, "user-data", "Pitstop"); var store = new FileStore(data);
            var first = Profile(Rule("B"), Rule("A")); first.Name = "My custom setup";
            var second = Profile(Rule("Flight")); var profiles = new List<Profile> { second, first };
            var expected = JsonSerializer.Serialize(profiles, FileStore.Json); store.SaveProfiles(profiles);
            var oldApp = Path.Combine(folder, "old-extract"); Directory.CreateDirectory(oldApp); File.WriteAllText(Path.Combine(oldApp, "Pitstop.exe"), "old build");
            var engine = new SessionEngine(new FakeHost(), store); await engine.StartAsync(first); await engine.ResetAsync();
            Directory.Delete(oldApp, true); Directory.CreateDirectory(Path.Combine(folder, "new-extract"));
            var afterUpdate = new FileStore(data);
            Check(JsonSerializer.Serialize(afterUpdate.LoadProfiles(), FileStore.Json) == expected, "Update/reset changed custom profiles, IDs, or app order");
            Check(File.Exists(store.ProfilesPath) && File.Exists(store.ProfilesBackupPath), "Stable profile files are missing");
        }
        finally { Directory.Delete(folder, true); }
    }
    public static Task MissingProfileRecovery()
    {
        var folder = TestFolder();
        try
        {
            var store = new FileStore(folder); var p = Profile(Rule("Custom")); store.SaveProfiles([p]);
            p.Name = "Latest saved name"; store.SaveProfiles([p]); File.Delete(store.ProfilesPath);
            var recovered = new FileStore(folder); Check(recovered.LoadProfiles().Single().Name == p.Name, "Recovery lost the latest save");
            Check(recovered.ProfileStorageNotice?.Contains("recovered") == true, "Recovery was not reported");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    public static Task DamagedProfileRecovery()
    {
        var folder = TestFolder();
        try
        {
            var store = new FileStore(folder); var p = Profile(Rule("Custom")); store.SaveProfiles([p]);
            File.WriteAllText(store.ProfilesPath, "{ interrupted write");
            var loaded = new FileStore(folder).LoadProfiles(); Check(loaded.Single().Id == p.Id, "Backup did not recover the custom profile");
            var damaged = Directory.GetFiles(folder, "profiles.damaged-*.json").Single();
            Check(File.ReadAllText(damaged) == "{ interrupted write", "The damaged original was discarded");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    public static Task LegacyProfileBackup()
    {
        var folder = TestFolder();
        try
        {
            var p = Profile(Rule("Legacy")); FileStore.Write(Path.Combine(folder, "profiles.json.bak"), new List<Profile> { p });
            Check(new FileStore(folder).LoadProfiles().Single().Id == p.Id, "Legacy backup was replaced by starters");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    public static Task UnrecoverableProfiles()
    {
        var folder = TestFolder();
        try
        {
            var store = new FileStore(folder); store.SaveProfiles([Profile(Rule("Custom"))]);
            File.WriteAllText(store.ProfilesPath, "broken primary"); File.WriteAllText(store.ProfilesBackupPath, "broken backup");
            try { store.LoadProfiles(); throw new Exception("Expected preserved corrupt-data error"); } catch (InvalidDataException) { }
            Check(File.ReadAllText(store.ProfilesPath) == "broken primary" && File.ReadAllText(store.ProfilesBackupPath) == "broken backup", "Unrecoverable profiles were overwritten");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    public static Task BackupFailure()
    {
        var folder = TestFolder();
        try
        {
            var store = new FileStore(folder);
            Directory.CreateDirectory(store.ProfilesBackupPath); // Simulate an unwritable backup destination.
            var profile = Profile(Rule("Custom")); store.SaveProfiles([profile]);
            Check(new FileStore(folder).LoadProfiles().Single().Id == profile.Id, "Backup error lost the primary save");
            Check(store.ProfileStorageNotice?.Contains("could not be updated") == true, "Backup failure was hidden");
            Directory.Delete(store.ProfilesBackupPath);
            store.SaveProfiles([profile]);
            Check(store.ProfileStorageNotice is null && File.Exists(store.ProfilesBackupPath), "Backup recovery left a stale error");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    public static Task EmptyProfilesPersist()
    {
        var folder = TestFolder();
        try
        {
            var store = new FileStore(folder); store.SaveProfiles([]);
            Check(new FileStore(folder).LoadProfiles().Count == 0, "An intentional empty list was repopulated");
            File.Delete(store.ProfilesPath); Check(new FileStore(folder).LoadProfiles().Count == 0, "Empty recovery copy was ignored");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
}
