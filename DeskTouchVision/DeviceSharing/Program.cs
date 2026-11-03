using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

internal static class Program
{
    // A second desktop-shortcut launch must reopen the already-running tray
    // instance. FindWindow(window title) is unreliable because auto-updating
    // temporarily changes the title; registered Windows messages reach
    // hidden top-level form windows without creating a second input hook.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message,
        IntPtr wParam, IntPtr lParam);

    internal static readonly int ShowDisplaysMessage = (int)RegisterWindowMessage(
        "DeskTouchVision.WindowsDisplays.ShowFromTray.2026");

    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true,
            @"Local\DeskTouchVision.WindowsDisplays", out bool firstInstance);
        if (!firstInstance)
        {
            const int HwndBroadcast = 0xFFFF;
            if (ShowDisplaysMessage != 0)
                PostMessage((IntPtr)HwndBroadcast, (uint)ShowDisplaysMessage,
                    IntPtr.Zero, IntPtr.Zero);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new DisplaySettingsForm());
    }
}
