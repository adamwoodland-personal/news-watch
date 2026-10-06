using Microsoft.Win32;

namespace NewsWatch.Services;

/// <summary>
/// Tracks whether the Windows session is locked, so alerts stay silent while
/// nobody is at the screen (panels still show and are logged as usual).
/// </summary>
public static class SessionLock
{
    public static bool IsLocked { get; private set; }

    /// <summary>Call once at startup, on the UI thread (the events need its message loop).</summary>
    public static void Start()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>SystemEvents is static: unhook on exit so it doesn't hold the app.</summary>
    public static void Stop()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) IsLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) IsLocked = false;
    }
}
