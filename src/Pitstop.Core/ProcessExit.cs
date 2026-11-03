namespace Pitstop.Core;

// Kept separate from Windows interop so tray, background, hung, and denied-exit
// behavior can be exercised without terminating any real user processes.
public interface IProcessLifetime
{
    bool IsRunning { get; }
    bool RequestWindowClose();
    Task<bool> WaitForExitAsync(TimeSpan timeout);
    void Terminate();
}

public static class ProcessExit
{
    public static async Task<CloseResult> CloseAsync(IProcessLifetime process, bool exitCompletely)
    {
        if (!process.IsRunning) return new(true, "already exited.");
        var requested = process.RequestWindowClose();
        if (requested && await process.WaitForExitAsync(TimeSpan.FromSeconds(3))) return new(true, "exited normally.");
        if (!process.IsRunning) return new(true, "exited normally.");
        if (!exitCompletely)
            return new(false, "its process is still running (possibly in the tray). Change Close behavior to Exit completely, or exit it manually.");
        process.Terminate();
        return await process.WaitForExitAsync(TimeSpan.FromSeconds(5))
            ? new(true, "process terminated; it is no longer running in the tray or background.")
            : new(false, "its process is still running after termination was requested.");
    }
}
