using Microsoft.Win32;

namespace NewsWatch.Services;

/// <summary>
/// Auto-run on login via HKCU\...\Run. The registry is the source of truth
/// (not settings.json) because the exe path is machine-specific.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NewsWatch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns false if the registry write failed.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe == null) return false;
                key.SetValue(ValueName, CommandLine(exe));
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunch the way this session was started: a custom settings file (as a
    /// full path, since the login working directory differs) and monitor carry over.
    /// </summary>
    private static string CommandLine(string exe)
    {
        var command = $"\"{exe}\" --minimized";
        if (App.SettingsFile != null) command += $" --settings \"{SettingsService.FilePath}\"";
        if (App.MonitorOverride is int monitor) command += $" --monitor {monitor}";
        return command;
    }
}
