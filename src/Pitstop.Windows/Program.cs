using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Pitstop.Core;

namespace Pitstop.Windows;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Pitstop-" + WindowsIdentity.GetCurrent().User?.Value, out var first);
        if (!first) { MessageBox.Show("Pitstop is already running. Look for it in the system tray.", "Pitstop"); return; }
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Pitstop;component/Theme.xaml", UriKind.Relative) });
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message + "\n\nAny saved session record is kept in %LocalAppData%\\Pitstop.", "Pitstop", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pitstop");
            app.Run(new MainWindow(new FileStore(data), new WindowsProcessHost()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n\nPitstop has not changed your apps. Existing data has been preserved.", "Unable to open Pitstop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
