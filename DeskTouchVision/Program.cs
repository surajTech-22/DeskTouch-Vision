using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DeskTouchVision;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Device sharing is now the primary native Windows-style experience.
        // Camera recognition remains on hold and is accessible only through
        // the explicit developer command: DeskTouchVision.exe --camera-experiment
        if (args.Any(a => a.Equals("--camera-experiment",
            StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new MainForm());
            return;
        }
        var share = Path.Combine(AppContext.BaseDirectory,
            "DeviceSharing", "DeskTouch.DeviceShare.exe");
        if (!File.Exists(share))
        {
            // Graceful fallback for older installations without the module.
            Application.Run(new MainForm());
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(share)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(share)!
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unable to start Windows display sharing: " + ex.Message,
                "DeskTouch Vision", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
