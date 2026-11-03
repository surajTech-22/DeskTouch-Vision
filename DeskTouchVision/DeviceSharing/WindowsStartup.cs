using Microsoft.Win32;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Per-Windows-user login startup. Reuses the installed DeskTouch sharing app
/// and its --tray switch, never creates a second service, updater or listener.
/// The user's off choice is recorded and not silently overwritten on update.
/// </summary>
internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskTouchVision";
    private static string ChoiceFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "windows-startup.choice");

    internal static string Command(string fullPath) =>
        "\"" + Path.GetFullPath(fullPath).Replace("\"", "") + "\" --tray";

    internal static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string value &&
                value.Contains("DeskTouch.DeviceShare.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static void EnsureDefault()
    {
        // The explicit user request is auto launch; honor any later user's
        // conscious disable choice even after an application update.
        if (File.Exists(ChoiceFile)) return;
        SetEnabled(true);
    }

    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Windows per-user Startup Apps key unavailable.");
        if (enabled) key.SetValue(ValueName, Command(Application.ExecutablePath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
        Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile)!);
        File.WriteAllText(ChoiceFile, enabled ? "enabled" : "disabled");
    }
}
