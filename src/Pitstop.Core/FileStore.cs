using System.Text.Json;

namespace Pitstop.Core;

public sealed class FileStore : ISessionStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; }
    public FileStore(string root) { Root = root; Directory.CreateDirectory(root); }
    private string ActivePath => Path.Combine(Root, "active-session.json");

    public List<Profile> LoadProfiles()
    {
        var path = Path.Combine(Root, "profiles.json");
        if (!File.Exists(path)) { var defaults = StarterProfiles.Create(); SaveProfiles(defaults); return defaults; }
        return Read<List<Profile>>(path);
    }
    public void SaveProfiles(List<Profile> profiles) => Write(Path.Combine(Root, "profiles.json"), profiles);
    public SessionState? LoadActive()
    {
        if (!File.Exists(ActivePath)) return null;
        var state = Read<SessionState>(ActivePath);
        if (state.Status == "Completed") { Complete(state); return null; }
        return state;
    }
    public void SaveActive(SessionState session) => Write(ActivePath, session);
    public void Complete(SessionState session)
    {
        var history = Path.Combine(Root, "history");
        Directory.CreateDirectory(history);
        Write(Path.Combine(history, $"{session.StartedAt:yyyyMMdd-HHmmss}-{session.Id}.json"), session);
        if (File.Exists(ActivePath)) File.Delete(ActivePath);
        if (File.Exists(ActivePath + ".bak")) File.Delete(ActivePath + ".bak");
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
