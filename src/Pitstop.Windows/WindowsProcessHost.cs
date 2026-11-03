using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Pitstop.Core;

namespace Pitstop.Windows;

public sealed class WindowsProcessHost : IProcessHost
{
    private readonly int sessionId = Process.GetCurrentProcess().SessionId;
    private readonly string self = Environment.ProcessPath ?? "";
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "explorer", "sihost", "fontdrvhost", "audiodg", "spoolsv", "taskhostw", "conhost", "SecurityHealthService",
        "MsMpEng", "MsSense", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "Pitstop"
    };

    public Task<IReadOnlyList<ProcessSnapshot>> CaptureAsync() => Task.Run<IReadOnlyList<ProcessSnapshot>>(() =>
    {
        var result = new List<ProcessSnapshot>();
        try
        {
            using var search = new ManagementObjectSearcher($"SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE SessionId = {sessionId}");
            search.Options.Timeout = TimeSpan.FromSeconds(15);
            using var objects = search.Get();
            foreach (ManagementObject item in objects)
            using (item)
            {
                try
                {
                    var pid = Convert.ToInt32(item["ProcessId"]);
                    var info = NativeProcess.Inspect(pid);
                    result.Add(new ProcessSnapshot
                    {
                        Pid = pid, ParentPid = Convert.ToInt32(item["ParentProcessId"]), ExecutablePath = info.Path,
                        StartedUtcTicks = info.Ticks,
                        Arguments = ExtractArguments(item["CommandLine"] as string),
                        HasWindow = NativeProcess.HasWindow(pid)
                    });
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or ManagementException or NotSupportedException)
                { /* Inaccessible or exited process: never attempt to control it. */ }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Windows process inventory is unavailable. No new session can start safely. Check that Windows Management Instrumentation is running.", ex);
        }
        return result;
    });

    public void Validate(AppRule rule, bool forLaunch)
    {
        var path = Expand(rule.ProcessPath);
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{rule.Name}: choose the full path to its process .exe.");
        if (IsProtected(path)) throw new InvalidOperationException($"{rule.Name}: Windows components and Pitstop itself cannot be managed.");
        if (!File.Exists(path)) throw new FileNotFoundException($"{rule.Name}: process executable not found. Edit this app and choose its location.", path);
        rule.ProcessPath = Path.GetFullPath(path);
        if (!forLaunch) return;
        var target = Expand(rule.LaunchTarget);
        if (!IsSteamTarget(target) && !IsAppsFolderTarget(target))
        {
            if (!Path.IsPathFullyQualified(target) || !File.Exists(target))
                throw new FileNotFoundException($"{rule.Name}: launch target not found. Choose an .exe or a shortcut.", target);
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{rule.Name}: use an .exe, .lnk, Steam launch link, or AppsFolder target.");
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsProtected(target))
                throw new InvalidOperationException($"{rule.Name}: a Windows component cannot be used as a launch target.");
            rule.LaunchTarget = Path.GetFullPath(target);
        }
        if (!string.IsNullOrWhiteSpace(rule.WorkingDirectory) && !Directory.Exists(Expand(rule.WorkingDirectory)))
            throw new DirectoryNotFoundException($"{rule.Name}: working directory not found.");
    }

    public Task<LaunchResult> LaunchAsync(AppRule rule) => Task.Run(() =>
    {
        Validate(rule, true);
        var target = Expand(rule.LaunchTarget);
        var start = new ProcessStartInfo { FileName = target, Arguments = rule.Arguments, UseShellExecute = true };
        if (IsAppsFolderTarget(target))
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            start.Arguments = target;
        }
        start.WorkingDirectory = !string.IsNullOrWhiteSpace(rule.WorkingDirectory) ? Expand(rule.WorkingDirectory)
            : File.Exists(target) ? Path.GetDirectoryName(target)! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var process = Process.Start(start);
        if (process is null) return new LaunchResult(null);
        try
        {
            var info = NativeProcess.Inspect(process.Id);
            return new LaunchResult(new ProcessSnapshot
            {
                Pid = process.Id, StartedUtcTicks = info.Ticks, ExecutablePath = info.Path
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        { return new LaunchResult(null); }
    });

    public async Task<CloseResult> CloseAsync(ProcessSnapshot snapshot, bool force)
    {
        if (IsProtected(snapshot.ExecutablePath)) return new(false, "protected process; left running.");
        try
        {
            using var process = NativeProcess.Open(snapshot, sessionId);
            if (process is null) return new(true, "original process exited; any replacement was left running.");
            return await ProcessExit.CloseAsync(process, force);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        { return new(false, $"Windows denied access to PID {snapshot.Pid}. If this app runs as administrator, restart Pitstop as administrator and retry restore. Services in another Windows session are not managed."); }
        catch (Win32Exception ex) { return new(false, $"Windows error {ex.NativeErrorCode}: {ex.Message}"); }
        catch (InvalidOperationException ex) { return new(false, ex.Message); }
    }

    public Task WaitAsync(TimeSpan delay) => Task.Delay(delay);
    public bool IsProtected(string path)
    {
        var full = Path.GetFullPath(Expand(path));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Paths.Equal(full, self) || full.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            || ProtectedNames.Contains(Path.GetFileNameWithoutExtension(full));
    }
    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
    public static bool IsSteamTarget(string target) => Regex.IsMatch(target, @"^steam://(rungameid|run)/[0-9]+/?$", RegexOptions.IgnoreCase);
    public static bool IsAppsFolderTarget(string target) => Regex.IsMatch(target, @"^shell:AppsFolder\\[A-Za-z0-9_.!\-]+$", RegexOptions.IgnoreCase);
    public static string? ExtractArguments(string? commandLine)
    {
        if (commandLine is null) return null;
        commandLine = commandLine.TrimStart();
        if (commandLine.Length == 0) return "";
        var end = commandLine[0] == '"' ? commandLine.IndexOf('"', 1) + 1 : commandLine.IndexOfAny([' ', '\t']);
        if (end <= 0) return "";
        return commandLine[end..].TrimStart();
    }
    public static (string Target, string Arguments, string WorkingDirectory) ReadShortcut(string path)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut reader is unavailable.");
        var shell = Activator.CreateInstance(type)!;
        object? shortcut = null;
        try
        {
            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
            string Read(string property) => (string?)shortcut.GetType().InvokeMember(property, BindingFlags.GetProperty, null, shortcut, null) ?? "";
            return (Read("TargetPath"), Read("Arguments"), Read("WorkingDirectory"));
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
