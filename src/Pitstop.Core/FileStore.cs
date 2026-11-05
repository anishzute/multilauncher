using System.Text.Json;

namespace Pitstop.Core;

public sealed class FileStore : ISessionStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; }
    public string ProfilesPath => Path.Combine(Root, "profiles.json");
    public string ProfilesBackupPath => Path.Combine(Root, "profiles.backup.json");
    public string? ProfileStorageNotice { get; private set; }
    private bool backupFailed;
    public FileStore(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    private string ActivePath => Path.Combine(Root, "active-session.json");

    public List<Profile> LoadProfiles()
    {
        Exception? damaged = null;
        if (File.Exists(ProfilesPath))
        {
            try
            {
                var profiles = ReadProfiles(ProfilesPath);
                BackupProfiles(profiles);
                return profiles;
            }
            catch (InvalidDataException ex) { damaged = ex; }
        }
        var backups = new[] { ProfilesBackupPath, ProfilesPath + ".bak", ProfilesBackupPath + ".bak" }
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        foreach (var backup in backups)
        {
            List<Profile> profiles;
            try { profiles = ReadProfiles(backup); }
            catch (InvalidDataException ex) { damaged ??= ex; continue; }
            string? preserved = null;
            if (File.Exists(ProfilesPath))
            {
                preserved = Path.Combine(Root, $"profiles.damaged-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                File.Move(ProfilesPath, preserved);
            }
            SaveProfiles(profiles);
            ProfileStorageNotice = $"Profiles recovered from {Path.GetFileName(backup)}."
                + (preserved is null ? "" : $" The damaged file was preserved as {Path.GetFileName(preserved)}.");
            return profiles;
        }
        if (damaged is not null || backups.Count > 0)
            throw new InvalidDataException($"Cannot recover profiles in {Root}. Existing files were preserved. Restore a valid exported profiles JSON file; no starter profiles have overwritten your data.", damaged);
        var defaults = StarterProfiles.Create(); SaveProfiles(defaults); return defaults;
    }
    public void SaveProfiles(List<Profile> profiles)
    {
        ValidateProfiles(profiles);
        Write(ProfilesPath, profiles);
        BackupProfiles(profiles);
    }
    private void BackupProfiles(List<Profile> profiles)
    {
        try
        {
            Write(ProfilesBackupPath, profiles);
            if (backupFailed) ProfileStorageNotice = null;
            backupFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { backupFailed = true; ProfileStorageNotice = $"Profiles are saved, but the recovery copy could not be updated: {ex.Message}"; }
    }
    public static List<Profile> ReadProfiles(string path)
    {
        var profiles = Read<List<Profile>>(path);
        ValidateProfiles(profiles); return profiles;
    }
    private static void ValidateProfiles(List<Profile> profiles)
    {
        if (profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.OpenApps is null || p.CloseApps is null
            || p.OpenApps.Concat(p.CloseApps).Any(a => a is null || string.IsNullOrWhiteSpace(a.Name))))
            throw new InvalidDataException("The profiles file contains invalid entries. No profiles have been discarded.");
    }
    public SessionState? LoadActive()
    {
        if (!File.Exists(ActivePath)) return null;
        var state = Read<SessionState>(ActivePath);
        if (state.Status is "Completed" or "Abandoned") { Complete(state); return null; }
        return state;
    }
    public void SaveActive(SessionState session) => Write(ActivePath, session);
    public void Complete(SessionState session)
    {
        var history = Path.Combine(Root, "history");
        Directory.CreateDirectory(history);
        Write(Path.Combine(history, $"{session.StartedAt:yyyyMMdd-HHmmss}-{session.Id}.json"), session);
        if (File.Exists(ActivePath)) File.Delete(ActivePath);
        try { if (File.Exists(ActivePath + ".bak")) File.Delete(ActivePath + ".bak"); }
        catch (IOException) { } // A leftover backup must not resurrect a finished session.
        catch (UnauthorizedAccessException) { }
    }
    public List<ActivityEntry> ReadHistory()
    {
        var folder = Path.Combine(Root, "history");
        if (!Directory.Exists(folder)) return [];
        var activity = new List<ActivityEntry>();
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderDescending().Take(20))
        {
            try { activity.AddRange(Read<SessionState>(file).Activity); }
            catch (Exception ex) { activity.Add(new(DateTimeOffset.Now, "Warning", $"Could not read history: {ex.Message}")); }
        }
        return activity.OrderByDescending(a => a.At).Take(1000).ToList();
    }
    public static T Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("The file is empty."); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Cannot read {path}. Your file has been preserved. Check its .bak copy before replacing it.", ex);
        }
    }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, value, Json);
            file.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }
}
