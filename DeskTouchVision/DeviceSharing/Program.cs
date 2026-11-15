using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using DeskTouchVision;

namespace DeskTouch.DeviceShare;

internal static class Program
{
    internal const string SingleInstanceMutexName = SingleInstanceActivation.SingleInstanceMutexName;
    internal const string ShowEventName = SingleInstanceActivation.ShowEventName;
    internal const string ExitEventName = SingleInstanceActivation.ExitEventName;

    // A second desktop-shortcut launch must reopen the already-running tray
    // instance. FindWindow(window title) is unreliable because auto-updating
    // temporarily changes the title; registered Windows messages plus a per-user
    // kernel event reliably reach hidden top-level form windows without creating
    // a second input hook.
    internal static readonly int ShowDisplaysMessage = SingleInstanceActivation.ShowDisplaysMessage;

    internal static bool ShouldShowExistingInstance(string[] args) =>
        !args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

    internal static void SignalExistingInstanceToShow() =>
        SingleInstanceActivation.SignalExistingHandlesBestEffort();

    [STAThread]
    private static void Main(string[] args)
    {
        bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var singleInstance = new Mutex(true, SingleInstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            singleInstance.Dispose();
            singleInstance = null;
            if (!ShouldShowExistingInstance(args))
                return;

            Mutex? gate = SingleInstanceActivation.AcquireActivationGate(TimeSpan.FromSeconds(5));
            bool acquired = false;
            try
            {
                if (SingleInstanceActivation.WakeOrRecoverExistingForInteractiveLaunch(Environment.ProcessId))
                    return;

                singleInstance = new Mutex(false, SingleInstanceMutexName);
                try { acquired = singleInstance.WaitOne(2000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    singleInstance.Dispose();
                    return;
                }
            }
            finally
            {
                SingleInstanceActivation.ReleaseActivationGate(gate);
            }
        }

        using var ownedInstanceLock = singleInstance;
        ApplicationConfiguration.Initialize();
        var form = new DisplaySettingsForm(startInTray);
        form.EnableLifecycleSignals();
        Application.Run(form);
    }
}
