using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DeskTouchVision;
internal static class Program
{
    private const uint ASFW_ANY = 0xFFFFFFFF;
    private const string ShowEventName = @"Local\DeskTouchVision.WindowsDisplays.ShowEvent.2026";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message,
        IntPtr wParam, IntPtr lParam);

    private static bool TryWakeRunningDisplays()
    {
        try { _ = AllowSetForegroundWindow(ASFW_ANY); } catch { }
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
            {
                using (showEvent) showEvent.Set();
                uint msg = RegisterWindowMessage("DeskTouchVision.WindowsDisplays.ShowFromTray.2026");
                if (msg != 0)
                    PostMessage((IntPtr)0xFFFF, msg, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
        }
        catch { }
        return false;
    }

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
        bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTray && TryWakeRunningDisplays())
            return;
        try
        {
            if (!startInTray)
                try { _ = AllowSetForegroundWindow(ASFW_ANY); } catch { }
            var launch = new ProcessStartInfo(share)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(share)!
            };
            if (args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
                launch.ArgumentList.Add("--tray");
            using var started = Process.Start(launch);
            // Keep the foreground launcher alive briefly while Windows creates
            // the target UI message loop so focus is transferred cleanly.
            if (!startInTray && started != null)
            {
                try { if (!started.HasExited) started.WaitForInputIdle(2500); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unable to start Windows display sharing: " + ex.Message,
                "DeskTouch Vision", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
