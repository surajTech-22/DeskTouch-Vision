using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

internal static class Program
{
    private const string WindowTitle = "DeskTouch Vision — Displays";

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int showCommand);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true,
            @"Local\DeskTouchVision.WindowsDisplays", out bool firstInstance);
        if (!firstInstance)
        {
            // Launching the desktop shortcut repeatedly should reopen the
            // existing Windows-style Displays window, not start another
            // receiver and duplicate global input hooks.
            var existing = FindWindow(null, WindowTitle);
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, 9); // SW_RESTORE
                SetForegroundWindow(existing);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new DisplaySettingsForm());
    }
}
