namespace Pitstop.Core;

public static class ProcessFamily
{
    public static List<ProcessSnapshot> Find(AppRule rule, IEnumerable<ProcessSnapshot> known,
        IReadOnlyList<ProcessSnapshot> running, IReadOnlyCollection<ProcessIdentity>? excluded = null)
    {
        var references = known.DistinctBy(p => p.Identity).ToList();
        bool Allowed(ProcessSnapshot p) => excluded?.Contains(p.Identity) != true;
        var owned = running.Where(p => Allowed(p) && references.Any(k => k.Identity == p.Identity
            && Paths.Equal(k.ExecutablePath, p.ExecutablePath))).ToList();
        if (!rule.IncludeChildProcesses) return owned;
        var folder = Paths.Normalize(rule.ProcessPath);
        var separator = folder.LastIndexOf('\\');
        folder = separator < 0 ? "" : folder[..(separator + 1)];
        bool InAppFolder(ProcessSnapshot p) => folder.Length > 3 && Paths.Normalize(p.ExecutablePath)
            .StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        bool added;
        do
        {
            added = false;
            foreach (var child in running.Where(p => Allowed(p) && InAppFolder(p)))
            {
                if (owned.Any(o => o.Identity == child.Identity)) continue;
                var parent = references.FirstOrDefault(p => p.Pid == child.ParentPid && child.StartedUtcTicks >= p.StartedUtcTicks);
                if (parent is null) continue;
                var liveParent = running.FirstOrDefault(p => p.Pid == parent.Pid);
                if (liveParent is not null && liveParent.Identity != parent.Identity
                    && child.StartedUtcTicks >= liveParent.StartedUtcTicks) continue;
                // A bootstrapper may already have exited. Recover its immediate
                // children, but don't follow an old PID into unrelated later work.
                if (!owned.Any(p => p.Identity == parent.Identity)
                    && child.StartedUtcTicks > parent.StartedUtcTicks + TimeSpan.FromSeconds(30).Ticks) continue;
                owned.Add(child); references.Add(child); added = true;
            }
        } while (added);
        return owned;
    }
}
