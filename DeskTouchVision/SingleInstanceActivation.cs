using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeskTouchVision;

/// <summary>
/// Ensures interactive launches of DeskTouch always result in a visible,
/// responsive Displays window on the caller's active desktop—reopening a
/// tray/minimized window when healthy, or recovering gracefully if an older
/// instance is stuck, closing, or isolated on another desktop.
/// </summary>
internal static class SingleInstanceActivation
{
    internal const string SingleInstanceMutexName = @"Local\DeskTouchVision.WindowsDisplays";
    internal const string ActivationGateMutexName = @"Local\DeskTouchVision.ActivationGate";
    internal const string ShowEventName = @"Local\DeskTouchVision.WindowsDisplays.ShowEvent.2026";
    internal const string ExitEventName = @"Local\DeskTouchVision.WindowsDisplays.ExitEvent.2026";
    internal const string TargetProcessName = "DeskTouch.DeviceShare";
    internal const string WindowTitlePrefix = "DeskTouch Vision";

    internal static readonly int ShowDisplaysMessage = (int)NativeMethods.RegisterWindowMessage(
        "DeskTouchVision.WindowsDisplays.ShowFromTray.2026");

    internal readonly record struct WindowMatch(
        IntPtr Handle,
        int ProcessId,
        bool Visible,
        bool Minimized,
        bool Responsive);

    internal enum WakeStepResult
    {
        NoExistingInstance,
        SurfacedOnCurrentDesktop,
        Waiting,
        NeedsRecovery
    }

    internal static bool IsDeskTouchMainWindowTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        title.StartsWith(WindowTitlePrefix, StringComparison.OrdinalIgnoreCase);

    internal static WakeStepResult EvaluateCurrentDesktopMatches(
        IReadOnlyCollection<WindowMatch> matches,
        bool anyPeerProcessAlive,
        TimeSpan elapsedSinceWakeSignal,
        TimeSpan? newestPeerAge)
    {
        if (matches.Any(m => m.Visible && !m.Minimized && m.Responsive))
            return WakeStepResult.SurfacedOnCurrentDesktop;

        if (!anyPeerProcessAlive)
            return WakeStepResult.NoExistingInstance;

        if (matches.Count == 0)
        {
            // Give a just-launched process up to 2.2s to create its HWND;
            // older processes with zero windows on the caller's desktop are
            // running on a different desktop or headless and cannot surface here.
            bool startingUp = newestPeerAge.HasValue && newestPeerAge.Value < TimeSpan.FromSeconds(4);
            TimeSpan noWindowTimeout = startingUp
                ? TimeSpan.FromMilliseconds(2200)
                : TimeSpan.FromMilliseconds(350);
            return elapsedSinceWakeSignal >= noWindowTimeout
                ? WakeStepResult.NeedsRecovery
                : WakeStepResult.Waiting;
        }

        return elapsedSinceWakeSignal >= TimeSpan.FromMilliseconds(1800)
            ? WakeStepResult.NeedsRecovery
            : WakeStepResult.Waiting;
    }

    internal static Mutex? AcquireActivationGate(TimeSpan timeout)
    {
        try
        {
            var gate = new Mutex(false, ActivationGateMutexName);
            bool acquired;
            try
            {
                acquired = gate.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
            {
                gate.Dispose();
                return null;
            }
            return gate;
        }
        catch
        {
            return null;
        }
    }

    internal static void ReleaseActivationGate(Mutex? gate)
    {
        if (gate == null) return;
        try { gate.ReleaseMutex(); } catch { }
        try { gate.Dispose(); } catch { }
    }

    internal static bool HasPathMismatchWithRunningPeers(
        string? expectedShareExecutablePath,
        IReadOnlyCollection<string?> peerPaths)
    {
        if (string.IsNullOrWhiteSpace(expectedShareExecutablePath)) return false;
        string expected = Path.GetFullPath(expectedShareExecutablePath);
        foreach (string? p in peerPaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                if (!string.Equals(Path.GetFullPath(p), expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Wakes an already-running DeskTouch instance on the caller's desktop and
    /// verifies its Displays window actually appears. If a background/different-
    /// desktop/different-path/hung instance is blocking startup without surfacing
    /// a visible window, shuts that instance down cleanly so the caller can launch fresh.
    /// Returns true if an existing window surfaced on the caller's desktop.
    /// </summary>
    internal static bool WakeOrRecoverExistingForInteractiveLaunch(
        int currentPid,
        string? expectedShareExecutablePath = null)
    {
        List<Process> peers = GetSameSessionShareProcesses(currentPid);
        try
        {
            bool kernelObjectsExist = HasExistingLifecycleHandles();
            if (peers.Count == 0 && !kernelObjectsExist)
                return false;

            if (!string.IsNullOrWhiteSpace(expectedShareExecutablePath))
            {
                var runningPaths = peers.Select(TryGetProcessPath).ToArray();
                if (HasPathMismatchWithRunningPeers(expectedShareExecutablePath, runningPaths))
                {
                    RecoverBlockedInstances(currentPid, peers);
                    return false;
                }
            }

            SignalShow(peers, FindDeskTouchWindowsOnCurrentDesktop(ToPidSet(peers)));

            var sw = Stopwatch.StartNew();
            bool resentDirect = false;
            while (true)
            {
                List<WindowMatch> matches = FindDeskTouchWindowsOnCurrentDesktop(ToPidSet(peers));
                TimeSpan? newestAge = GetNewestProcessAge(peers);
                bool anyAlive = peers.Any(IsAliveSafe) || HasExistingLifecycleHandles();

                WakeStepResult step = EvaluateCurrentDesktopMatches(
                    matches, anyAlive, sw.Elapsed, newestAge);

                if (step == WakeStepResult.SurfacedOnCurrentDesktop)
                {
                    WindowMatch? surfaced = matches.FirstOrDefault(m =>
                        m.Visible && !m.Minimized && m.Responsive);
                    if (surfaced is { Handle: var hwnd } && hwnd != IntPtr.Zero)
                        ReinforceForeground(hwnd);
                    return true;
                }

                if (step == WakeStepResult.NoExistingInstance)
                    return false;

                if (step == WakeStepResult.NeedsRecovery)
                    break;

                if (!resentDirect && sw.ElapsedMilliseconds >= 200)
                {
                    resentDirect = true;
                    SignalShow(peers, matches);
                }

                Thread.Sleep(40);
                DisposeProcesses(peers);
                peers = GetSameSessionShareProcesses(currentPid);
            }

            // Recovery path: existing process is on another desktop, mid-shutdown,
            // or failed to surface a visible window on this desktop.
            RecoverBlockedInstances(currentPid, peers);
            return false;
        }
        finally
        {
            DisposeProcesses(peers);
        }
    }

    internal static void AllowSetForegroundWindowAny()
    {
        try { _ = NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY); } catch { }
    }

    internal static void SignalExistingHandlesBestEffort()
    {
        AllowSetForegroundWindowAny();
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
            {
                using (showEvent) showEvent.Set();
            }
        }
        catch { }
        if (ShowDisplaysMessage != 0)
        {
            NativeMethods.PostMessage(
                (IntPtr)NativeMethods.HwndBroadcast,
                (uint)ShowDisplaysMessage,
                IntPtr.Zero,
                IntPtr.Zero);
        }
    }

    private static void SignalShow(
        IReadOnlyCollection<Process> peers,
        IReadOnlyCollection<WindowMatch> currentDesktopMatches)
    {
        AllowSetForegroundWindowAny();
        foreach (Process peer in peers)
        {
            try { _ = NativeMethods.AllowSetForegroundWindow((uint)peer.Id); } catch { }
        }
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
            {
                using (showEvent) showEvent.Set();
            }
        }
        catch { }
        if (ShowDisplaysMessage != 0)
        {
            foreach (WindowMatch m in currentDesktopMatches)
            {
                NativeMethods.PostMessage(m.Handle, (uint)ShowDisplaysMessage, IntPtr.Zero, IntPtr.Zero);
            }
            NativeMethods.PostMessage(
                (IntPtr)NativeMethods.HwndBroadcast,
                (uint)ShowDisplaysMessage,
                IntPtr.Zero,
                IntPtr.Zero);
        }
    }

    private static void RecoverBlockedInstances(int currentPid, List<Process> initialPeers)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
            {
                using (exitEvent) exitEvent.Set();
            }
        }
        catch { }

        foreach (Process p in initialPeers)
        {
            try
            {
                if (!p.HasExited) _ = p.CloseMainWindow();
            }
            catch { }
        }

        var waitSw = Stopwatch.StartNew();
        while (waitSw.ElapsedMilliseconds < 3000)
        {
            List<Process> remaining = GetSameSessionShareProcesses(currentPid);
            bool anyRemaining = remaining.Count > 0;
            DisposeProcesses(remaining);
            if (!anyRemaining && !HasExistingSingleInstanceMutex())
                return;
            Thread.Sleep(50);
        }

        List<Process> stuck = GetSameSessionShareProcesses(currentPid);
        try
        {
            foreach (Process p in stuck)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: false);
                        p.WaitForExit(1500);
                    }
                }
                catch { }
            }
        }
        finally
        {
            DisposeProcesses(stuck);
        }

        // Also clear stale DeskTouchVision launcher processes holding the startup gate if any.
        int currentSession = Process.GetCurrentProcess().SessionId;
        foreach (Process launcher in Process.GetProcessesByName("DeskTouchVision"))
        {
            try
            {
                if (launcher.Id != currentPid && launcher.SessionId == currentSession && !launcher.HasExited)
                {
                    launcher.Kill(entireProcessTree: false);
                    launcher.WaitForExit(1000);
                }
            }
            catch { }
            finally { launcher.Dispose(); }
        }

        var cleanupSw = Stopwatch.StartNew();
        while (cleanupSw.ElapsedMilliseconds < 1000 && HasExistingSingleInstanceMutex())
        {
            Thread.Sleep(40);
        }
    }

    internal static List<WindowMatch> FindDeskTouchWindowsOnCurrentDesktop(IReadOnlyCollection<int> pids)
    {
        var results = new List<WindowMatch>();
        if (pids.Count == 0) return results;

        var pidSet = pids as HashSet<int> ?? new HashSet<int>(pids);
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pidSet.Contains((int)pid)) return true;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            _ = NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            if (!IsDeskTouchMainWindowTitle(sb.ToString())) return true;

            bool visible = NativeMethods.IsWindowVisible(hwnd);
            bool iconic = NativeMethods.IsIconic(hwnd);
            bool responsive = NativeMethods.SendMessageTimeout(
                hwnd,
                NativeMethods.WM_NULL,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG,
                120,
                out _) != IntPtr.Zero;

            results.Add(new WindowMatch(hwnd, (int)pid, visible, iconic, responsive));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static void ReinforceForeground(IntPtr hwnd)
    {
        try
        {
            _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            _ = NativeMethods.BringWindowToTop(hwnd);
            _ = NativeMethods.SetForegroundWindow(hwnd);
        }
        catch { }
    }

    private static bool HasExistingLifecycleHandles()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
            {
                showEvent.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException) { return true; }
        catch { }

        return HasExistingSingleInstanceMutex();
    }

    private static bool HasExistingSingleInstanceMutex()
    {
        try
        {
            if (Mutex.TryOpenExisting(SingleInstanceMutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException) { return true; }
        catch { }
        return false;
    }

    private static List<Process> GetSameSessionShareProcesses(int excludePid)
    {
        int currentSession = Process.GetCurrentProcess().SessionId;
        var list = new List<Process>();
        foreach (Process p in Process.GetProcessesByName(TargetProcessName))
        {
            try
            {
                if (p.Id == excludePid || p.SessionId != currentSession || p.HasExited)
                {
                    p.Dispose();
                    continue;
                }
                list.Add(p);
            }
            catch
            {
                p.Dispose();
            }
        }
        return list;
    }

    private static HashSet<int> ToPidSet(IReadOnlyCollection<Process> peers)
    {
        var set = new HashSet<int>();
        foreach (Process p in peers)
        {
            try { set.Add(p.Id); } catch { }
        }
        return set;
    }

    private static TimeSpan? GetNewestProcessAge(IReadOnlyCollection<Process> peers)
    {
        TimeSpan? newest = null;
        DateTime now = DateTime.UtcNow;
        foreach (Process p in peers)
        {
            try
            {
                TimeSpan age = now - p.StartTime.ToUniversalTime();
                if (!newest.HasValue || age < newest.Value)
                    newest = age;
            }
            catch { }
        }
        return newest;
    }

    private static string? TryGetProcessPath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    private static bool IsAliveSafe(Process p)
    {
        try { return !p.HasExited; }
        catch { return false; }
    }

    private static void DisposeProcesses(List<Process> list)
    {
        foreach (Process p in list)
        {
            try { p.Dispose(); } catch { }
        }
    }

    private static class NativeMethods
    {
        internal const int HwndBroadcast = 0xFFFF;
        internal const uint ASFW_ANY = 0xFFFFFFFF;
        internal const uint GW_OWNER = 4;
        internal const uint WM_NULL = 0x0000;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;
        internal const int SW_SHOW = 5;

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterWindowMessage(string messageName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr hwnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeoutMs,
            out IntPtr result);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hwnd, int command);
    }
}
