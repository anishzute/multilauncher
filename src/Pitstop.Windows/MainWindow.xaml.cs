using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Pitstop.Core;
using Forms = System.Windows.Forms;

namespace Pitstop.Windows;

public partial class MainWindow : Window
{
    private readonly FileStore store;
    private readonly WindowsProcessHost host;
    private readonly SessionEngine engine;
    private readonly List<Profile> profiles;
    private readonly Forms.NotifyIcon tray;
    private List<ActivityEntry> history;
    private bool busy;
    private bool resetting;
    private int resetGeneration;
    private bool quitting;
    private bool refreshing;
    private string page = "profiles";
    private Profile? Selected => ProfilesList.SelectedItem as Profile;

    public MainWindow(FileStore store, WindowsProcessHost host)
    {
        this.store = store; this.host = host;
        profiles = store.LoadProfiles();
        engine = new SessionEngine(host, store);
        history = store.ReadHistory();
        InitializeComponent();
        Ui.DarkTitleBar(this);
        var iconStream = Application.GetResourceStream(new Uri("/Loadout;component/Assets/loadout.ico", UriKind.Relative))!;
        using var stream = iconStream.Stream;
        tray = new Forms.NotifyIcon { Text = "Loadout · Your setup, one click away", Icon = new System.Drawing.Icon(stream), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Loadout", null, (_, _) => ShowFromTray());
        menu.Items.Add("End session & restore", null, async (_, _) => { ShowFromTray(); await EndSessionAsync(); });
        menu.Items.Add("Stop & reset session", null, async (_, _) => { ShowFromTray(); await ResetSessionAsync(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Loadout", null, async (_, _) => await ExitFromTrayAsync());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowFromTray();
        Closed += (_, _) => tray.Dispose();
        engine.Changed += () => { if (!quitting) Dispatcher.Invoke(RefreshSession); };
        RefreshProfiles(profiles.FirstOrDefault()?.Id);
        RefreshSession();
        RefreshStorage();
        if (engine.Current is not null) StatusText.Text = "Saved session recovered. Use End & restore when you’re ready.";
    }

    private void RefreshProfiles(string? selectId = null)
    {
        refreshing = true;
        var id = selectId ?? Selected?.Id;
        ProfilesList.ItemsSource = null;
        ProfilesList.ItemsSource = profiles;
        ProfilesList.SelectedItem = profiles.FirstOrDefault(p => p.Id == id) ?? profiles.FirstOrDefault();
        refreshing = false;
        RefreshProfile();
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || !IsInitialized) return;
        ShowPage("profiles"); RefreshProfile();
    }
    private void RefreshProfile()
    {
        var p = Selected;
        ProfileTitle.Text = p?.Name ?? "Your next setup";
        ProfileDescription.Text = p?.Description ?? "Create a profile to get started.";
        CategoryLabel.Text = (p?.Category ?? "NEW").ToUpperInvariant() + " PROFILE";
        OpenApps.ItemsSource = null; OpenApps.ItemsSource = p?.OpenApps;
        CloseApps.ItemsSource = null; CloseApps.ItemsSource = p?.CloseApps;
        OpenCount.Text = $"{p?.OpenApps.Count(a => a.Enabled) ?? 0} enabled · {p?.OpenApps.Count ?? 0} apps";
        CloseCount.Text = $"{p?.CloseApps.Count(a => a.Enabled) ?? 0} enabled · {p?.CloseApps.Count ?? 0} apps";
        OpenEmpty.Visibility = (p?.OpenApps.Count ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseEmpty.Visibility = (p?.CloseApps.Count ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupHint.Visibility = p is not null && p.OpenApps.Concat(p.CloseApps).Any(a => !a.Configured) ? Visibility.Visible : Visibility.Collapsed;
        AppLists.Margin = SetupHint.Visibility == Visibility.Collapsed ? new Thickness(0, 18, 0, 0) : new Thickness(0);
        RefreshSession();
        RefreshStorage();
    }
    private void RefreshSession()
    {
        if (quitting) return;
        var session = engine.Current;
        var working = busy || resetting;
        var idle = !working && session is null;
        if (Selected is { } selected)
        {
            foreach (var rule in selected.OpenApps)
            {
                var launched = session?.ProfileId == selected.Id ? session.Launched.FirstOrDefault(a => a.Rule.Id == rule.Id) : null;
                rule.SessionRole = launched is null ? "" : launched.WasAlreadyRunning ? "Already open — stays open on restore"
                    : launched.LaunchFailed ? "Launch failed — see activity" : launched.Tracked.Count == 0 ? "Process not detected — see activity" : "Opened for this session";
            }
        }
        StartButton.IsEnabled = idle && Selected is not null;
        AppLists.IsEnabled = idle && Selected is not null;
        EditProfileButton.IsEnabled = DuplicateButton.IsEnabled = DeleteProfileButton.IsEnabled = idle && Selected is not null;
        TopNewButton.IsEnabled = NewProfileButton.IsEnabled = idle;
        DragReorder.SetEnabled(ProfilesList, idle);
        EndButton.IsEnabled = !working && session is not null && session.Status != "Abandoned";
        ResetButton.IsEnabled = session is not null && !resetting;
        ResetButton.Content = resetting ? "Stopping…" : busy ? "Stop & reset" : "Reset session";
        SessionBanner.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        BusyBar.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        ConfirmUntrackedButton.Visibility = session?.Status == "Needs attention" && session.Launched.Any(a =>
            a.LaunchAttempted && !a.WasAlreadyRunning && !a.CloseFinished && a.Tracked.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;
        ConfirmUntrackedButton.IsEnabled = !working;
        if (session is not null)
        {
            var warning = session.Status is "Needs attention" or "Paused" or "Abandoned" || session.Activity.LastOrDefault()?.Level is "Warning" or "Error";
            SessionTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(warning ? (byte)235 : (byte)183, warning ? (byte)187 : (byte)219, warning ? (byte)140 : (byte)191));
            SessionTitle.Text = $"{session.ProfileName}  ·  {session.Status}";
            SessionDetail.Text = session.Activity.LastOrDefault()?.Message ?? "Saved session available to restore.";
            EndButton.Content = session.Status == "Needs attention" ? "↶  Retry restore" : session.Status == "Paused" ? "↶  Resume restore" : "■  End & restore";
            StatusText.Text = session.Activity.LastOrDefault()?.Message ?? "Session record saved.";
            tray.Text = "Loadout · " + session.ProfileName[..Math.Min(session.ProfileName.Length, 40)];
            if (resetting) SessionDetail.Text = "Stopping remaining steps. Waiting for any already-issued app action, then resetting the session.";
        }
        var entries = (session?.Activity.AsEnumerable().Reverse() ?? []).Concat(history).Take(1000).ToList();
        ActivityList.ItemsSource = entries.Count > 0 ? entries : new[] { new ActivityEntry(DateTimeOffset.Now, "Info", "Your session activity will appear here.") };
    }
    private void SaveProfiles()
    {
        store.SaveProfiles(profiles);
        RefreshProfiles();
        RefreshStorage();
    }
    private void List_Reordered(object sender, RoutedEventArgs e)
    {
        SaveProfiles();
        StatusText.Text = "New order saved.";
    }
    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine.Current is not null) return;
        var dialog = new ProfileDialog(this);
        if (dialog.ShowDialog() == true) { profiles.Add(dialog.Result); store.SaveProfiles(profiles); RefreshProfiles(dialog.Result.Id); ShowPage("profiles"); }
    }
    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        var dialog = new ProfileDialog(this, p);
        if (dialog.ShowDialog() == true) { profiles[profiles.IndexOf(p)] = dialog.Result; SaveProfiles(); }
    }
    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p, FileStore.Json), FileStore.Json)!;
        copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy";
        foreach (var app in copy.OpenApps.Concat(copy.CloseApps)) app.Id = Guid.NewGuid().ToString("N");
        profiles.Add(copy); store.SaveProfiles(profiles); RefreshProfiles(copy.Id);
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (MessageBox.Show(this, $"Delete the “{p.Name}” profile? Its apps will stay installed.", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        profiles.Remove(p); SaveProfiles();
    }
    private void AddOpen_Click(object sender, RoutedEventArgs e) => EditApp(null, false);
    private void AddClose_Click(object sender, RoutedEventArgs e) => EditApp(null, true);
    private void EditApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppRule rule } && Selected is { } p) EditApp(rule, p.CloseApps.Contains(rule));
    }
    private void EditApp(AppRule? rule, bool closeList)
    {
        if (Selected is not { } p || busy || engine.Current is not null) return;
        var dialog = new AppDialog(this, host, rule, closeList);
        if (dialog.ShowDialog() != true) return;
        var list = closeList ? p.CloseApps : p.OpenApps;
        var index = rule is null ? -1 : list.IndexOf(rule);
        switch (dialog.Action)
        {
            case "remove": if (index >= 0) list.RemoveAt(index); break;
            case "up": if (index > 0) (list[index - 1], list[index]) = (list[index], list[index - 1]); break;
            case "down": if (index >= 0 && index < list.Count - 1) (list[index + 1], list[index]) = (list[index], list[index + 1]); break;
            default: if (index < 0) list.Add(dialog.Result); else list[index] = dialog.Result; break;
        }
        SaveProfiles();
    }
    private void ToggleApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: AppRule rule } check || Selected is not { } p) return;
        rule.Enabled = check.IsChecked == true;
        if (rule.Enabled && !rule.Configured)
        {
            rule.Enabled = false;
            EditApp(rule, p.CloseApps.Contains(rule));
            RefreshProfile();
        }
        else SaveProfiles();
    }
    private async void StartSession_Click(object sender, RoutedEventArgs e)
    {
        if (busy || resetting || Selected is not { } p) return;
        busy = true; RefreshSession();
        try { store.SaveProfiles(profiles); await engine.StartAsync(p); }
        catch (Exception ex) { Ui.Error(this, ex); }
        finally { busy = false; RefreshSession(); }
    }
    private async void EndSession_Click(object sender, RoutedEventArgs e) => await EndSessionAsync();
    private async void ConfirmUntracked_Click(object sender, RoutedEventArgs e)
    {
        if (busy || resetting || engine.Current is null) return;
        if (MessageBox.Show(this, "Confirm that you have exited any apps marked as unidentified in this session. Loadout will then finish restoring your previous apps.",
            "Finish manual cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        busy = true; RefreshSession();
        try { await engine.ConfirmUntrackedAppsClosedAsync(); history = store.ReadHistory(); }
        catch (Exception ex) { Ui.Error(this, ex); }
        finally { busy = false; RefreshSession(); }
    }
    private async Task EndSessionAsync()
    {
        if (busy || resetting || engine.Current is null) return;
        var generation = resetGeneration;
        busy = true; RefreshSession();
        try
        {
            await engine.EndAsync();
            if (quitting || generation != resetGeneration) return;
            history = store.ReadHistory();
            if (engine.Current is null) { StatusText.Text = "Session finished. Your previous apps are back."; tray.Text = "Loadout · Ready when you are"; }
            else ShowPage("activity");
        }
        catch (Exception ex) { if (!quitting && generation == resetGeneration) Ui.Error(this, ex); }
        finally { busy = false; RefreshSession(); }
    }
    private void ShowPage(string name)
    {
        page = name;
        var selectedNav = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 40, 35));
        ProfilesNav.Background = name == "profiles" ? selectedNav : System.Windows.Media.Brushes.Transparent;
        ActivityNav.Background = name == "activity" ? selectedNav : System.Windows.Media.Brushes.Transparent;
        SettingsNav.Background = name == "settings" ? selectedNav : System.Windows.Media.Brushes.Transparent;
        ProfilesPage.Visibility = name == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = name == "activity" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = name == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = name switch { "activity" => "Session activity", "settings" => "Settings & help", _ => "Your profiles" };
        PageSubtitle.Text = name switch { "activity" => "Every action, in one place.", "settings" => "A few details about your desktop companion.", _ => "A different setup for every way you play." };
        TopNewButton.Visibility = name == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        RefreshSession();
    }
    private void ProfilesNav_Click(object sender, RoutedEventArgs e) => ShowPage("profiles");
    private void ActivityNav_Click(object sender, RoutedEventArgs e) => ShowPage("activity");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) { RefreshStorage(); ShowPage("settings"); }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "Loadout-profiles.json", Filter = "JSON profiles (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true)
        {
            try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profiles, FileStore.Json)); StatusText.Text = "Profiles exported."; }
            catch (Exception ex) { Ui.Error(this, ex); }
        }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine.Current is not null) { MessageBox.Show(this, "End the current session before importing profiles.", "Loadout"); return; }
        var dialog = new OpenFileDialog { Filter = "JSON profiles (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new InvalidDataException("This profile file is too large.");
            var imported = FileStore.Read<List<Profile>>(dialog.FileName);
            if (imported.Count > 100 || imported.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.OpenApps is null || p.CloseApps is null
                || p.OpenApps.Count + p.CloseApps.Count > 200 || p.OpenApps.Concat(p.CloseApps).Any(a => a is null || string.IsNullOrWhiteSpace(a.Name))))
                throw new InvalidDataException("This file does not contain valid Loadout profiles.");
            foreach (var p in imported)
            {
                p.Id = Guid.NewGuid().ToString("N");
                foreach (var rule in p.OpenApps.Concat(p.CloseApps)) rule.Id = Guid.NewGuid().ToString("N");
            }
            profiles.AddRange(imported); SaveProfiles();
            StatusText.Text = $"Imported {imported.Count} profiles. Review app paths and Force close settings before starting.";
        }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
    private void DataFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true });
    private void RefreshStorage()
    {
        ProfilesFilePath.Text = store.ProfilesPath;
        ProfilesBackupPath.Text = store.ProfilesBackupPath;
        StorageNotice.Text = store.ProfileStorageNotice ?? "Profiles save automatically outside the app folder. Replacing or moving Loadout.exe does not replace your profiles. Use the same Windows account, or import an exported backup on another account.";
    }
    private void ProfilesFile_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(store.ProfilesPath) { UseShellExecute = true });
    private async void ResetSession_Click(object sender, RoutedEventArgs e) => await ResetSessionAsync();
    private async Task<bool> ResetSessionAsync(bool confirm = true)
    {
        if (resetting) return false;
        if (engine.Current is null) return true;
        if (confirm && MessageBox.Show(this, "Stop and reset this session?\n\nRemaining restore steps will be canceled. Apps will be left as they are; you can clean them up manually. Your profiles are kept, and the session record is archived for reference.",
            "Reset session", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        resetGeneration++;
        resetting = true; RefreshSession();
        try
        {
            await engine.ResetAsync();
            if (quitting) return true;
            history = store.ReadHistory();
            StatusText.Text = "Session reset. Profiles kept; apps left as they are.";
            tray.Text = "Loadout · Ready when you are";
            return true;
        }
        catch (Exception ex) { Ui.Error(this, ex); return false; }
        finally { resetting = false; RefreshSession(); }
    }
    private async void Exit_Click(object sender, RoutedEventArgs e) => await ExitFromTrayAsync();
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (quitting || (!busy && !resetting && engine.Current is null)) return;
        e.Cancel = true; Hide();
        tray.ShowBalloonTip(2500, "Loadout is in the system tray", "Your session is still saved. Open Loadout from the tray to end and restore.", Forms.ToolTipIcon.Info);
    }
    private async Task ExitFromTrayAsync()
    {
        ShowFromTray();
        engine.RequestStop();
        if (engine.Current is not null)
        {
            var dialog = new SessionExitDialog(this);
            if (dialog.ShowDialog() != true) return;
            if (dialog.Choice == SessionExitChoice.ResetSession && !await ResetSessionAsync(false)) return;
        }
        engine.RequestStop();
        quitting = true; Close();
    }
}
