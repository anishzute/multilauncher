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
        var iconStream = Application.GetResourceStream(new Uri("/Pitstop;component/Assets/pitstop.ico", UriKind.Relative))!;
        using var stream = iconStream.Stream;
        tray = new Forms.NotifyIcon { Text = "Pitstop · Your setup, one click away", Icon = new System.Drawing.Icon(stream), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Pitstop", null, (_, _) => ShowFromTray());
        menu.Items.Add("End session & restore", null, async (_, _) => { ShowFromTray(); await EndSessionAsync(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Pitstop", null, async (_, _) => await ExitFromTrayAsync());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowFromTray();
        Closed += (_, _) => tray.Dispose();
        engine.Changed += () => Dispatcher.Invoke(RefreshSession);
        RefreshProfiles(profiles.FirstOrDefault()?.Id);
        RefreshSession();
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
    }
    private void RefreshSession()
    {
        var session = engine.Current;
        var idle = !busy && session is null;
        StartButton.IsEnabled = idle && Selected is not null;
        AppLists.IsEnabled = idle && Selected is not null;
        EditProfileButton.IsEnabled = DuplicateButton.IsEnabled = DeleteProfileButton.IsEnabled = idle && Selected is not null;
        TopNewButton.IsEnabled = NewProfileButton.IsEnabled = idle;
        EndButton.IsEnabled = !busy && session is not null;
        SessionBanner.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (session is not null)
        {
            var warning = session.Status == "Needs attention" || session.Activity.LastOrDefault()?.Level is "Warning" or "Error";
            SessionTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(warning ? (byte)235 : (byte)183, warning ? (byte)187 : (byte)219, warning ? (byte)140 : (byte)191));
            SessionTitle.Text = $"{session.ProfileName}  ·  {session.Status}";
            SessionDetail.Text = session.Activity.LastOrDefault()?.Message ?? "Saved session available to restore.";
            EndButton.Content = session.Status == "Needs attention" ? "↶  Retry restore" : "■  End & restore";
            StatusText.Text = session.Activity.LastOrDefault()?.Message ?? "Session record saved.";
            tray.Text = "Pitstop · " + session.ProfileName[..Math.Min(session.ProfileName.Length, 40)];
        }
        var entries = (session?.Activity.AsEnumerable().Reverse() ?? []).Concat(history).Take(1000).ToList();
        ActivityList.ItemsSource = entries.Count > 0 ? entries : new[] { new ActivityEntry(DateTimeOffset.Now, "Info", "Your session activity will appear here.") };
    }
    private void SaveProfiles()
    {
        store.SaveProfiles(profiles);
        RefreshProfiles();
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
        if (busy || Selected is not { } p) return;
        busy = true; RefreshSession();
        try { store.SaveProfiles(profiles); await engine.StartAsync(p); }
        catch (Exception ex) { Ui.Error(this, ex); }
        finally { busy = false; RefreshSession(); }
    }
    private async void EndSession_Click(object sender, RoutedEventArgs e) => await EndSessionAsync();
    private async Task EndSessionAsync()
    {
        if (busy || engine.Current is null) return;
        busy = true; RefreshSession();
        try
        {
            await engine.EndAsync();
            history = store.ReadHistory();
            if (engine.Current is null) { StatusText.Text = "Session finished. Your previous apps are back."; tray.Text = "Pitstop · Ready when you are"; }
            else ShowPage("activity");
        }
        catch (Exception ex) { Ui.Error(this, ex); }
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
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "Pitstop-profiles.json", Filter = "JSON profiles (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true)
        {
            try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profiles, FileStore.Json)); StatusText.Text = "Profiles exported."; }
            catch (Exception ex) { Ui.Error(this, ex); }
        }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine.Current is not null) { MessageBox.Show(this, "End the current session before importing profiles.", "Pitstop"); return; }
        var dialog = new OpenFileDialog { Filter = "JSON profiles (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new InvalidDataException("This profile file is too large.");
            var imported = FileStore.Read<List<Profile>>(dialog.FileName);
            if (imported.Count > 100 || imported.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.OpenApps is null || p.CloseApps is null
                || p.OpenApps.Count + p.CloseApps.Count > 200 || p.OpenApps.Concat(p.CloseApps).Any(a => a is null || string.IsNullOrWhiteSpace(a.Name))))
                throw new InvalidDataException("This file does not contain valid Pitstop profiles.");
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
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (quitting || (!busy && engine.Current is null)) return;
        e.Cancel = true; Hide();
        tray.ShowBalloonTip(2500, "Pitstop is in the system tray", "Your session is still saved. Open Pitstop from the tray to end and restore.", Forms.ToolTipIcon.Info);
    }
    private async Task ExitFromTrayAsync()
    {
        ShowFromTray();
        if (busy) { MessageBox.Show(this, "Wait for the current session action to finish before exiting.", "Pitstop"); return; }
        if (engine.Current is not null)
        {
            var answer = MessageBox.Show(this, "End the session and restore your previous apps before exiting?\n\nYes: restore, then exit.\nNo: exit now and keep the saved session for later.", "Exit Pitstop", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) { await EndSessionAsync(); if (engine.Current is not null) return; }
        }
        quitting = true; Close();
    }
}
