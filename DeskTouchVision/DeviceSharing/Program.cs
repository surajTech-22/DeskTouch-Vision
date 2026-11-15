using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

internal static class Program
{
    internal const string SingleInstanceMutexName = @"Local\DeskTouchVision.WindowsDisplays";
    internal const string ShowEventName = @"Local\DeskTouchVision.WindowsDisplays.ShowEvent.2026";
    internal const string ExitEventName = @"Local\DeskTouchVision.WindowsDisplays.ExitEvent.2026";

    // A second desktop-shortcut launch must reopen the already-running tray
    // instance. FindWindow(window title) is unreliable because auto-updating
    // temporarily changes the title; registered Windows messages plus a per-user
    // kernel event reliably reach hidden top-level form windows without creating
    // a second input hook.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message,
        IntPtr wParam, IntPtr lParam);

    internal static readonly int ShowDisplaysMessage = (int)RegisterWindowMessage(
        "DeskTouchVision.WindowsDisplays.ShowFromTray.2026");

    internal static bool ShouldShowExistingInstance(string[] args) =>
        !args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

    internal static void SignalExistingInstanceToShow()
    {
        try { _ = Win.AllowSetForegroundWindow(Win.ASFW_ANY); } catch { }
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
            {
                using (showEvent) showEvent.Set();
            }
        }
        catch { }
        const int HwndBroadcast = 0xFFFF;
        if (ShowDisplaysMessage != 0)
            PostMessage((IntPtr)HwndBroadcast, (uint)ShowDisplaysMessage,
                IntPtr.Zero, IntPtr.Zero);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        using var singleInstance = new Mutex(true,
            SingleInstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            if (ShouldShowExistingInstance(args))
                SignalExistingInstanceToShow();
            return;
        }

        ApplicationConfiguration.Initialize();
        var form = new DisplaySettingsForm(startInTray);
        form.EnableLifecycleSignals();
        Application.Run(form);
    }
}
