using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Pitstop.Core;

namespace Pitstop.Windows;

// Limited-information queries avoid MainModule's VM_READ requirement. Every
// operation is bound to a verified kernel handle, not just a recyclable PID.
internal sealed class NativeProcess : IProcessLifetime, IDisposable
{
    private const uint Query = 0x1000, Synchronize = 0x100000, TerminateAccess = 1;
    private readonly SafeProcessHandle handle;
    private readonly ProcessSnapshot snapshot;
    public static NativeProcess? Open(ProcessSnapshot snapshot, int sessionId)
    {
        var handle = OpenProcess(Query | Synchronize, false, snapshot.Pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error == 87) return null; // PID no longer exists.
            throw new Win32Exception(error);
        }
        try
        {
            if (!ProcessIdToSessionId(snapshot.Pid, out var actualSession)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (actualSession != sessionId || CreationTicks(handle) != snapshot.StartedUtcTicks) { handle.Dispose(); return null; }
            if (!Paths.Equal(ImagePath(handle), snapshot.ExecutablePath)) throw new InvalidOperationException("The executable identity changed; it was left running.");
            return new NativeProcess(handle, snapshot);
        }
        catch { handle.Dispose(); throw; }
    }
    private NativeProcess(SafeProcessHandle handle, ProcessSnapshot snapshot) { this.handle = handle; this.snapshot = snapshot; }
    public static (string Path, long Ticks) Inspect(int pid)
    {
        using var handle = OpenProcess(Query, false, pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (ImagePath(handle), CreationTicks(handle));
    }
    public static bool HasWindow(int pid)
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != pid || !IsWindowVisible(window)) return true;
            found = true; return false;
        }, IntPtr.Zero);
        return found;
    }
    private static long CreationTicks(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return DateTime.FromFileTimeUtc(created).Ticks;
    }
    private static string ImagePath(SafeProcessHandle handle)
    {
        var buffer = new StringBuilder(32768); var size = buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }
    public bool IsRunning
    {
        get
        {
            var result = WaitForSingleObject(handle, 0);
            if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result == 258;
        }
    }
    public bool RequestWindowClose()
    {
        if (!IsRunning) return false;
        var posted = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid == snapshot.Pid && IsRunning) posted |= PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return posted;
    }
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (IsRunning && DateTime.UtcNow < until) await Task.Delay(100);
        return !IsRunning;
    }
    public void Terminate()
    {
        if (!IsRunning) return;
        using var terminator = OpenProcess(Query | Synchronize | TerminateAccess, false, snapshot.Pid);
        if (terminator.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (CreationTicks(terminator) != snapshot.StartedUtcTicks) return;
        if (!Paths.Equal(ImagePath(terminator), snapshot.ExecutablePath)) throw new InvalidOperationException("The executable identity changed; it was left running.");
        if (!TerminateProcess(terminator, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => handle.Dispose();

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
