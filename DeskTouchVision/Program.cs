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
            // Never show the old camera experiment for a damaged installation:
            // this made the user think the v0.2.0 UI was still the main app.
            MessageBox.Show(
                "DeskTouch Windows Displays is missing from this installation.\\n\\n" +
                "Repair using the complete latest ZIP from:\\n" +
                "https://github.com/surajTech-22/DeskTouch-Vision/releases/latest\\n\\n" +
                "Extract it and run INSTALL_DESKTOUCH.cmd.",
                "DeskTouch Vision — repair installation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var launch = new ProcessStartInfo(share)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(share)!
            };
            if (args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
                launch.ArgumentList.Add("--tray");
            Process.Start(launch);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unable to start Windows display sharing: " + ex.Message,
                "DeskTouch Vision", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
