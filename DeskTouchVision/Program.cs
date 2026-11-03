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
                "The Windows Displays component is missing from this installation." +
                Environment.NewLine + Environment.NewLine +
                "Missing: " + share + Environment.NewLine + Environment.NewLine +
                "The download or installation may be incomplete, or antivirus may have " +
                "quarantined the file." + Environment.NewLine +
                "Check Windows Security > Virus & threat protection > Protection history." +
                Environment.NewLine + Environment.NewLine +
                "Run CHECK_SECURITY_STATUS.ps1 from the official DeskTouch ZIP to " +
                "create a read-only report in Downloads." + Environment.NewLine +
                "Official package: https://github.com/surajTech-22/DeskTouch-Vision/releases/latest" +
                Environment.NewLine + Environment.NewLine +
                "Do not disable antivirus protection or add a blanket folder exclusion.",
                "DeskTouch Vision — Windows Displays file missing",
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
