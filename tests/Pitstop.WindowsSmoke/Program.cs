using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pitstop.Core;
using Pitstop.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the published test-worker folder.");
        return RunAsync(Path.GetFullPath(args[0])).GetAwaiter().GetResult();
    }
    private static async Task<int> RunAsync(string worker)
    {
        var root = Path.Combine(Path.GetTempPath(), "Pitstop-Verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = new WindowsProcessHost();
        var passed = 0;
        try
        {
            // This runs before the first await, on the entry thread's STA.
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Loadout;component/Theme.xaml", UriKind.Relative) });
            var uiStore = new FileStore(Path.Combine(root, "ui-data"));
            var fixturePath = Path.Combine(worker, "Pitstop.TestWorker.exe");
            uiStore.SaveProfiles([new Profile { Name = "UI verification", OpenApps = [new AppRule { Name = "Icon and row template", LaunchTarget = fixturePath, ProcessPath = fixturePath }] }]);
            var window = new MainWindow(uiStore, host);
            window.Show(); window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.Close();
            Console.WriteLine("PASS  WPF window, resources, app templates, and tray icon initialize on Windows"); passed++;

            // Exercise the actual Exit button and explicit keep-session choice
            // while a failed restore record is present. No retry is requested.
            var uiProfile = uiStore.LoadProfiles().Single();
            uiStore.SaveActive(new SessionState { ProfileId = uiProfile.Id, ProfileName = uiProfile.Name, Status = "Needs attention" });
            var failedWindow = new MainWindow(uiStore, host);
            failedWindow.Show(); failedWindow.UpdateLayout();
            if (failedWindow.FindName("ResetButton") is not Button { IsEnabled: true }) throw new Exception("Failed restore has no enabled reset action.");
            if (failedWindow.FindName("ProfilesFilePath") is not TextBox pathBox || pathBox.Text != uiStore.ProfilesPath) throw new Exception("Stable JSON path is not shown in Settings.");
            var chooseExit = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<SessionExitDialog>().Single();
                var keep = LogicalTreeHelper.FindLogicalNode(dialog, "KeepSessionExitButton") as Button
                    ?? throw new Exception("Explicit keep-session exit action is missing.");
                keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            ((Button)failedWindow.FindName("ExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            chooseExit.Task.GetAwaiter().GetResult();
            if (failedWindow.IsVisible || uiStore.LoadActive()?.Status != "Needs attention" || uiStore.LoadProfiles().Single().Id != uiProfile.Id)
                throw new Exception("Exit retried restore, discarded the session, or changed profiles.");
            Console.WriteLine("PASS  Failed-restore UI exposes Reset and exits while keeping its session and profiles without retry"); passed++;

            AppRule Install(string folder, string name, string arguments = "--background")
            {
                var target = Path.Combine(root, folder); Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(worker)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                var executable = Path.Combine(target, name + ".exe");
                File.Copy(Path.Combine(target, "Pitstop.TestWorker.exe"), executable, true);
                return new AppRule { Name = name, LaunchTarget = executable, ProcessPath = executable, Arguments = arguments, DelaySeconds = 0 };
            }
            async Task<ProcessSnapshot> WaitFor(AppRule rule, bool windowRequired = false)
            {
                for (var i = 0; i < 100; i++)
                {
                    var match = (await host.CaptureAsync()).FirstOrDefault(p => Paths.Equal(p.ExecutablePath, rule.ProcessPath) && (!windowRequired || p.HasWindow));
                    if (match is not null) return match;
                    await Task.Delay(100);
                }
                throw new Exception("Test worker did not appear: " + rule.Name);
            }
            static void Check(bool value, string why) { if (!value) throw new Exception(why); }

            var tray = Install("tray", "TrayFixture", "--tray");
            // Shell icons are extracted on the original STA before any await.
            var icon = new AppIconConverter().Convert(tray, typeof(ImageSource), null!, CultureInfo.InvariantCulture);
            Check(icon is ImageSource, "Executable icon was not extracted.");
            Console.WriteLine("PASS  Native executable icon extraction"); passed++;
            // No dispatcher work is needed after this point. Do not capture an
            // STA synchronization context while Main waits for async checks.
            SynchronizationContext.SetSynchronizationContext(null);
            await host.LaunchAsync(tray);
            var trayProcess = await WaitFor(tray, true);
            var graceful = await host.CloseAsync(trayProcess, false);
            Check(!graceful.Closed && (await host.CaptureAsync()).Any(p => p.Identity == trayProcess.Identity), "Graceful-only should leave the hidden tray fixture alive.");
            var complete = await host.CloseAsync(trayProcess, true);
            Check(complete.Closed && !(await host.CaptureAsync()).Any(p => p.Identity == trayProcess.Identity), "Complete exit did not terminate the tray fixture.");
            Console.WriteLine("PASS  A real app that hides on WM_CLOSE is terminated by complete exit"); passed++;

            var background = Install("background", "BackgroundFixture");
            await host.LaunchAsync(background); var bg = await WaitFor(background);
            Check((await host.CloseAsync(bg, true)).Closed, "Windowless background fixture did not exit.");
            Check(!(await host.CaptureAsync()).Any(p => p.Identity == bg.Identity), "Background process still exists.");
            Console.WriteLine("PASS  Real windowless background-process termination"); passed++;

            var plex = Install("session/background", "PlexFixture");
            var race = Install("session/game", "GameFixture", "--tray");
            await host.LaunchAsync(plex); var original = await WaitFor(plex);
            var session = new SessionEngine(host, new FileStore(Path.Combine(root, "session-data")));
            await session.StartAsync(new Profile { Name = "Windows verification", OpenApps = [race], CloseApps = [plex] });
            await WaitFor(race, true);
            Check(!(await host.CaptureAsync()).Any(p => p.Identity == original.Identity), "Session start did not close the background app.");
            await session.EndAsync();
            Check(session.Current is null, "Session did not finish.");
            var restored = await WaitFor(plex);
            Check(restored.Identity != original.Identity && !(await host.CaptureAsync()).Any(p => Paths.Equal(p.ExecutablePath, race.ProcessPath)), "Session restore set is wrong.");
            Console.WriteLine("PASS  Real Windows session: close background app, launch tray app, exit tray app, restore background app"); passed++;

            var parent = Install("family", "ParentFixture", "--tray");
            var helper = Install("family/resources", "BundledHelper");
            var external = Install("external", "IndependentHelper");
            parent.Arguments += $" --spawn-child \"{helper.LaunchTarget}\" --spawn-child \"{external.LaunchTarget}\"";
            var familySession = new SessionEngine(host, new FileStore(Path.Combine(root, "family-data")));
            await familySession.StartAsync(new Profile { Name = "Family verification", OpenApps = [parent] });
            await WaitFor(helper); var outside = await WaitFor(external);
            await familySession.EndAsync();
            var remaining = await host.CaptureAsync();
            Check(familySession.Current is null && !remaining.Any(p => Paths.Equal(p.ExecutablePath, helper.ProcessPath)), "Bundled helper stayed alive.");
            Check(remaining.Any(p => p.Identity == outside.Identity), "Unrelated child outside the installation folder was terminated.");
            Console.WriteLine("PASS  Bundled helper exits; external child is preserved"); passed++;

            var resetStore = new FileStore(Path.Combine(root, "reset-data"));
            var resetPlex = Install("reset/background", "ResetBackgroundFixture");
            var resetGame = Install("reset/game", "ResetGameFixture");
            var custom = new Profile { Name = "Persistent custom profile", OpenApps = [resetGame], CloseApps = [resetPlex] };
            resetStore.SaveProfiles([custom]);
            var savedJson = File.ReadAllText(resetStore.ProfilesPath);
            await host.LaunchAsync(resetPlex); await WaitFor(resetPlex);
            var resetSession = new SessionEngine(host, resetStore); await resetSession.StartAsync(custom);
            resetSession.Current!.Stopped.Single().Restart.LaunchTarget = Path.Combine(root, "missing-restart-target.exe");
            await resetSession.EndAsync();
            Check(resetSession.Current?.Status == "Needs attention", "The restore failure was not retained.");
            var beforeReset = (await host.CaptureAsync()).Where(p => p.ExecutablePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)).Select(p => p.Identity).ToHashSet();
            await resetSession.ResetAsync();
            var afterReset = (await host.CaptureAsync()).Where(p => p.ExecutablePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)).Select(p => p.Identity).ToHashSet();
            Check(resetSession.Current is null && beforeReset.SetEquals(afterReset), "Reset changed processes or kept the failed session active.");
            Check(File.ReadAllText(resetStore.ProfilesPath) == savedJson && new SessionEngine(host, resetStore).Current is null, "Reset lost profiles or returned after restart.");
            Console.WriteLine("PASS  Failed restore resets without process changes and preserves profile JSON across restart"); passed++;
            File.Delete(resetStore.ProfilesPath);
            var upgraded = new FileStore(resetStore.Root); var recoveredProfiles = upgraded.LoadProfiles();
            Check(recoveredProfiles.Single().Id == custom.Id && File.ReadAllText(upgraded.ProfilesPath) == savedJson, "Latest JSON backup did not recover after the app reopened.");
            Console.WriteLine("PASS  Persistent profile JSON is recovered from its latest backup on Windows"); passed++;
            Console.WriteLine($"\n{passed}/9 Windows smoke checks passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL  " + ex); return 1; }
        finally
        {
            foreach (var p in (await host.CaptureAsync()).Where(p => p.ExecutablePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                await host.CloseAsync(p, true);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
