using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Visible, user-controllable Windows Startup-folder shortcut. We no longer
/// add a silent HKCU Run persistence entry. The user's explicit startup choice
/// survives application updates, and only our old exact Run command is removed.
/// No scheduled task, service, administrator privilege or antivirus exclusion.
/// </summary>
internal static class WindowsStartup
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "DeskTouchVision";
    internal static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "DeskTouch Vision.lnk");
    private static string ChoiceFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "windows-startup.choice");

    // Legacy command format stays available for regression tests/migration.
    internal static string Command(string fullPath) =>
        "\"" + Path.GetFullPath(fullPath).Replace("\"", "") + "\" --tray";

    internal static string ExpectedExecutable => Path.GetFullPath(Application.ExecutablePath);

    private static bool OwnsLegacyEntry(string? command) =>
        command != null &&
        string.Equals(command.Trim(), Command(ExpectedExecutable),
            StringComparison.OrdinalIgnoreCase);

    private static bool LegacyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, false);
        return OwnsLegacyEntry(key?.GetValue(LegacyValueName) as string);
    }

    internal static bool IsOwnedExecutableTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            string full = Path.GetFullPath(target);
            if (string.Equals(full, ExpectedExecutable, StringComparison.OrdinalIgnoreCase))
                return true;
            string shareDir = Path.GetDirectoryName(ExpectedExecutable) ?? "";
            string appDir = Path.GetDirectoryName(shareDir) ?? "";
            string launcher = string.IsNullOrEmpty(appDir) ? "" : Path.Combine(appDir, "DeskTouchVision.exe");
            return !string.IsNullOrEmpty(launcher) &&
                   string.Equals(full, Path.GetFullPath(launcher), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // A link in the current user's Startup Apps folder, not a registry autorun.
    internal static bool Enabled
    {
        get
        {
            if (!File.Exists(ShortcutPath)) return false;
            try
            {
                dynamic shell = Activator.CreateInstance(
                    Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Windows shortcut service unavailable."))!;
                dynamic link = shell.CreateShortcut(ShortcutPath);
                string target = link.TargetPath;
                string args = link.Arguments;
                return string.Equals(Path.GetFullPath(target), ExpectedExecutable,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(args.Trim(), "--tray", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    internal static bool IsOwnedShortcutPresent()
    {
        if (!File.Exists(ShortcutPath)) return false;
        try
        {
            dynamic shell = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("Windows shortcut service unavailable."))!;
            dynamic link = shell.CreateShortcut(ShortcutPath);
            return IsOwnedExecutableTarget(link.TargetPath);
        }
        catch { return false; }
    }

    internal static void EnsureDefault()
    {
        bool previouslyDisabled = File.Exists(ChoiceFile) &&
            string.Equals(File.ReadAllText(ChoiceFile).Trim(), "disabled",
                StringComparison.OrdinalIgnoreCase);
        if (previouslyDisabled)
        {
            // A previously disabled startup must never be silently re-enabled.
            RemoveOwnedLegacyEntry();
            return;
        }

        // Existing enabled state and first-run default both explicitly open
        // in the tray. The checkbox in the tray can disable this at any time.
        if (!Enabled) SetEnabled(true);
        else RemoveOwnedLegacyEntry();
    }

    private static void RemoveOwnedLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true);
        if (key != null && OwnsLegacyEntry(key.GetValue(LegacyValueName) as string))
            key.DeleteValue(LegacyValueName, false);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var directory = Path.GetDirectoryName(ShortcutPath)
                ?? throw new InvalidOperationException("Windows Startup folder is unavailable.");
            Directory.CreateDirectory(directory);

            // Do not overwrite a link owned by a different application.
            if (File.Exists(ShortcutPath) && !IsOwnedShortcutPresent())
                throw new IOException("Startup shortcut already exists but does not belong to DeskTouch.");
            dynamic shell = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("Windows shortcut service unavailable."))!;
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = ExpectedExecutable;
            shortcut.Arguments = "--tray";
            shortcut.WorkingDirectory = Path.GetDirectoryName(ExpectedExecutable);
            shortcut.Description = "DeskTouch Vision — authorized device sharing in the system tray";
            shortcut.Save();
        }
        else if (Enabled || IsOwnedShortcutPresent())
        {
            File.Delete(ShortcutPath);
        }

        // Only remove the legacy key if it exactly targets this installed app.
        RemoveOwnedLegacyEntry();
        Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile)!);
        File.WriteAllText(ChoiceFile, enabled ? "enabled" : "disabled");
    }
}
