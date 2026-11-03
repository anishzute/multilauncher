using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--background") >= 0) { Thread.Sleep(Timeout.Infinite); return; }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form = new Form { Text = "Pitstop disposable test app", Width = 330, Height = 140 };
        form.Controls.Add(new Label { Text = "Test fixture: closing this window hides it.\nThe verification process cleans it up.", AutoSize = true, Left = 20, Top = 20 });
        form.FormClosing += (_, e) => { e.Cancel = true; form.Hide(); };
        form.Shown += (_, _) =>
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "--spawn-child") Process.Start(new ProcessStartInfo(args[++i], "--background") { UseShellExecute = false, CreateNoWindow = true });
        };
        Application.Run(form);
    }
}
