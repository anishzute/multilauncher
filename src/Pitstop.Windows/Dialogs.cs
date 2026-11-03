using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Pitstop.Core;

namespace Pitstop.Windows;

public static class Ui
{
    public static Brush Muted => (Brush)Application.Current.FindResource("Muted");
    public static TextBlock Text(string text, double size = 13, bool muted = false) => new()
    { Text = text, FontSize = size, Foreground = muted ? Muted : Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap };
    public static TextBox Input(string text) => new() { Text = text, Margin = new Thickness(0, 6, 0, 15) };
    public static void Field(Panel panel, string label, UIElement input)
    { panel.Children.Add(Text(label, 12)); panel.Children.Add(input); }
    public static Button Button(string title, RoutedEventHandler action, bool primary = false)
    {
        var button = new Button { Content = title, Margin = new Thickness(8, 0, 0, 0), MinWidth = 85 };
        if (primary) button.Style = (Style)Application.Current.FindResource("Primary");
        button.Click += action;
        return button;
    }
    public static void DarkTitleBar(Window window)
    {
        window.Style ??= (Style)Application.Current.FindResource(typeof(Window));
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width - 32);
        window.MinHeight = Math.Min(window.MinHeight, area.Height - 32);
        window.Width = Math.Min(window.Width, area.Width - 32);
        window.Height = Math.Min(window.Height, area.Height - 32);
        window.SourceInitialized += (_, _) => { var dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int)); };
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void Error(Window owner, Exception ex) => MessageBox.Show(owner, ex.Message, "Pitstop", MessageBoxButton.OK, MessageBoxImage.Information);
}

public sealed class ProfileDialog : Window
{
    public Profile Result { get; private set; }
    public ProfileDialog(Window owner, Profile? source = null)
    {
        Result = source is null ? new Profile() : JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(source, FileStore.Json), FileStore.Json)!;
        Owner = owner; Title = source is null ? "New profile" : "Edit profile";
        Width = 500; Height = 510; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.DarkTitleBar(this);
        var stack = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        stack.Children.Add(Ui.Text(Title, 23));
        stack.Children.Add(new Border { Height = 22 });
        var name = Ui.Input(source?.Name ?? "");
        var description = Ui.Input(source?.Description ?? "");
        var category = new ComboBox { ItemsSource = new[] { "Racing", "Flight", "Gaming", "Work" }, SelectedItem = Result.Category, Margin = new Thickness(0, 6, 0, 24) };
        Ui.Field(stack, "Profile name", name);
        Ui.Field(stack, "A short description", description);
        Ui.Field(stack, "Category", category);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Ui.Button("Cancel", (_, _) => DialogResult = false));
        buttons.Children.Add(Ui.Button("Save profile", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            Result.Name = name.Text.Trim(); Result.Description = description.Text.Trim(); Result.Category = category.SelectedItem as string ?? "Racing";
            DialogResult = true;
        }, true));
        stack.Children.Add(buttons);
        Loaded += (_, _) => name.Focus();
    }
}

public sealed class AppDialog : Window
{
    public AppRule Result { get; private set; }
    public string Action { get; private set; } = "save";
    private readonly TextBox target;
    private readonly TextBox processPath;
    private readonly TextBox arguments;
    private readonly TextBox working;
    private readonly TextBox name;
    private readonly WindowsProcessHost host;
    public AppDialog(Window owner, WindowsProcessHost host, AppRule? source, bool closeList)
    {
        this.host = host;
        Result = source is null ? new AppRule() : JsonSerializer.Deserialize<AppRule>(JsonSerializer.Serialize(source, FileStore.Json), FileStore.Json)!;
        Owner = owner; Title = source is null ? "Add app" : "Edit app";
        Width = 710; Height = 790; MinHeight = 650; MinWidth = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.DarkTitleBar(this);
        var root = new Grid { Margin = new Thickness(26, 23, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(Ui.Text(closeList ? "Make room while you play." : "Add it to your setup.", 24));
        heading.Children.Add(new TextBlock { Text = closeList ? "Choose the app to close and how to reopen it afterward." : "Choose how this app launches and exits at the end of a session.", Foreground = Ui.Muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(heading);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var form = new StackPanel { Margin = new Thickness(0, 0, 12, 8) }; scroll.Content = form;
        name = Ui.Input(source?.Name ?? "");
        Ui.Field(form, "Display name", name);
        target = Ui.Input(Result.LaunchTarget);
        processPath = Ui.Input(Result.ProcessPath);
        arguments = Ui.Input(Result.Arguments);
        working = Ui.Input(Result.WorkingDirectory);
        form.Children.Add(Ui.Text("Launch target  ·  executable, shortcut, or launch link", 12));
        form.Children.Add(BrowseRow(target, BrowseTarget));
        form.Children.Add(Ui.Text("Process executable  ·  the actual app .exe to track", 12));
        form.Children.Add(BrowseRow(processPath, BrowseProcess));
        var pick = Ui.Button("Pick a running app…", PickRunning);
        pick.Margin = new Thickness(0, -5, 0, 16); pick.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(pick);
        Ui.Field(form, "Launch arguments  ·  optional", arguments);
        Ui.Field(form, "Working folder  ·  optional", working);
        var enabled = new CheckBox { Content = "Include this app in the profile", IsChecked = source?.Configured == true ? source.Enabled : true, Margin = new Thickness(0, 4, 0, 15) };
        var force = new CheckBox { Content = closeList ? "Force close if graceful exit does not work" : "Force close this app when ending the session", IsChecked = Result.ForceClose };
        form.Children.Add(enabled); form.Children.Add(force);
        form.Children.Add(new TextBlock { Text = "Force close can lose unsaved work. Background apps without a window often need it. It applies only to this executable, in your Windows session.", Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(38, 8, 0, 16) });
        var delay = Ui.Input(Result.DelaySeconds.ToString()); delay.Width = 80; delay.HorizontalAlignment = HorizontalAlignment.Left;
        if (!closeList) Ui.Field(form, "Wait after launching  ·  seconds, 0–60", delay);
        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        if (source is not null)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            actions.Children.Add(Ui.Button("↑ Move", (_, _) => { Action = "up"; DialogResult = true; }));
            actions.Children.Add(Ui.Button("↓ Move", (_, _) => { Action = "down"; DialogResult = true; }));
            actions.Children.Add(Ui.Button("Remove", (_, _) => { Action = "remove"; DialogResult = true; }));
            footer.Children.Add(actions);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Ui.Button("Cancel", (_, _) => DialogResult = false));
        buttons.Children.Add(Ui.Button("Save app", (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Enter a display name.");
                if (!int.TryParse(delay.Text, out var seconds) || seconds is < 0 or > 60) throw new InvalidOperationException("Choose a delay from 0 to 60 seconds.");
                Result.Name = name.Text.Trim(); Result.LaunchTarget = WindowsProcessHost.Expand(target.Text); Result.ProcessPath = WindowsProcessHost.Expand(processPath.Text);
                Result.Arguments = arguments.Text; Result.WorkingDirectory = working.Text.Trim().Trim('"');
                Result.Enabled = enabled.IsChecked == true; Result.ForceClose = force.IsChecked == true; Result.DelaySeconds = seconds;
                host.Validate(Result, true);
                DialogResult = true;
            }
            catch (Exception ex) { Ui.Error(this, ex); }
        }, true));
        footer.Children.Add(buttons);
        Loaded += (_, _) => name.Focus();
    }
    private static Grid BrowseRow(TextBox input, RoutedEventHandler action)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(input);
        var browse = Ui.Button("Browse…", action); browse.Margin = new Thickness(8, 6, 0, 15); Grid.SetColumn(browse, 1); grid.Children.Add(browse);
        return grid;
    }
    private void BrowseTarget(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a launch target", Filter = "Apps and shortcuts (*.exe;*.lnk)|*.exe;*.lnk", DereferenceLinks = false };
        if (dialog.ShowDialog(this) != true) return;
        var prior = target.Text;
        target.Text = dialog.FileName;
        if (dialog.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var shortcut = WindowsProcessHost.ReadShortcut(dialog.FileName);
                if (shortcut.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processPath.Text = shortcut.Target;
                if (string.IsNullOrWhiteSpace(working.Text)) working.Text = shortcut.WorkingDirectory;
                // Leave shortcut arguments in the shortcut itself; do not append them twice.
            }
            catch (Exception ex) { Ui.Error(this, ex); }
        }
        else if (string.IsNullOrWhiteSpace(processPath.Text) || Paths.Equal(prior, processPath.Text)) processPath.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }
    private void BrowseProcess(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the process executable", Filter = "Windows executable (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) processPath.Text = dialog.FileName;
    }
    private void PickRunning(object sender, RoutedEventArgs e)
    {
        var dialog = new ProcessPicker(this, host);
        if (dialog.ShowDialog() != true || dialog.Selected is null) return;
        var process = dialog.Selected;
        processPath.Text = process.ExecutablePath;
        if (string.IsNullOrWhiteSpace(target.Text))
        {
            target.Text = process.ExecutablePath;
            arguments.Text = process.Arguments ?? "";
        }
        if (string.IsNullOrWhiteSpace(name.Text))
        {
            try { name.Text = FileVersionInfo.GetVersionInfo(process.ExecutablePath).ProductName ?? Path.GetFileNameWithoutExtension(process.ExecutablePath); }
            catch { name.Text = Path.GetFileNameWithoutExtension(process.ExecutablePath); }
        }
    }
}

public sealed class ProcessPicker : Window
{
    public ProcessSnapshot? Selected { get; private set; }
    private sealed record Choice(ProcessSnapshot Process)
    {
        public override string ToString() => $"{Path.GetFileName(Process.ExecutablePath)}\n{Process.ExecutablePath}";
    }
    public ProcessPicker(Window owner, WindowsProcessHost host)
    {
        Owner = owner; Title = "Pick a running app"; Width = 740; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.DarkTitleBar(this);
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = grid;
        grid.Children.Add(Ui.Text("Choose an app that’s running on this PC.", 19));
        var search = Ui.Input(""); search.Margin = new Thickness(0, 16, 0, 12); search.ToolTip = "Filter by name or path"; Grid.SetRow(search, 1); grid.Children.Add(search);
        var list = new ListBox(); Grid.SetRow(list, 2); grid.Children.Add(list);
        var footer = new Grid { Margin = new Thickness(0, 15, 0, 0) }; Grid.SetRow(footer, 3); grid.Children.Add(footer);
        var status = Ui.Text("Reading running apps…", 11, true); status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(status);
        void Accept() { if (list.SelectedItem is Choice item) { Selected = item.Process; DialogResult = true; } }
        var choose = Ui.Button("Use selected app", (_, _) => Accept(), true); choose.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(choose);
        list.MouseDoubleClick += (_, _) => Accept();
        List<Choice> choices = [];
        search.TextChanged += (_, _) => list.ItemsSource = choices.Where(c => c.ToString().Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        Loaded += async (_, _) =>
        {
            try
            {
                choices = (await host.CaptureAsync()).Where(p => !host.IsProtected(p.ExecutablePath))
                    .DistinctBy(p => p.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => Path.GetFileName(p.ExecutablePath), StringComparer.OrdinalIgnoreCase).Select(p => new Choice(p)).ToList();
                list.ItemsSource = choices;
                status.Text = $"{choices.Count} apps · Windows components excluded";
            }
            catch (Exception ex) { status.Text = "Unable to read running apps."; Ui.Error(this, ex); }
        };
    }
}
