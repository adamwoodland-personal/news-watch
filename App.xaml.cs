using System.Windows;

namespace NewsWatch;

public partial class App : System.Windows.Application
{
    /// <summary>Start hidden in the tray (--minimized / /min).</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>1-based monitor for the news panels (--monitor N); null = primary.</summary>
    public static int? MonitorOverride { get; private set; }

    /// <summary>Alternate settings file (--settings path); null = %APPDATA%\NewsWatch\settings.json.</summary>
    public static string? SettingsFile { get; private set; }

    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per user (Global\ + username spans sessions of the same user).
        _instanceMutex = new Mutex(true, $"Global\\NewsWatch.{Environment.UserName}", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "NEWS//WATCH is already running — look for the cyan page icon in the system tray.",
                "NEWS//WATCH", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ParseArgs(e.Args);

        // Feed content is untrusted and arrives all day: log anything unexpected
        // and keep running rather than vanish from the tray.
        DispatcherUnhandledException += (_, ex) =>
        {
            LogError("Unhandled", ex.Exception);
            ex.Handled = true;
        };

        var window = new MainWindow();
        MainWindow = window; // keeps ShutdownMode=OnMainWindowClose working even when hidden
        if (!StartMinimized)
            window.Show();
    }

    /// <summary>Appends to errors.log in the config folder; best effort.</summary>
    public static void LogError(string context, Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.SettingsService.Dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(Services.SettingsService.Dir, "errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // nowhere left to report it
        }
    }

    private static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].TrimStart('-', '/').ToLowerInvariant();

            if (arg is "minimized" or "minimised" or "min")
            {
                StartMinimized = true;
            }
            else if (arg.StartsWith("monitor"))
            {
                // Accept "--monitor 2", "--monitor=2", "/monitor:2"
                var value = arg.Length > "monitor".Length
                    ? arg["monitor".Length..].TrimStart('=', ':')
                    : (i + 1 < args.Length ? args[++i] : "");
                if (int.TryParse(value, out var n) && n >= 1)
                    MonitorOverride = n;
            }
            else if (arg.StartsWith("settings"))
            {
                // Accept "--settings C:\x.json" or "--settings=C:\x.json"; the raw arg keeps its case.
                var raw = args[i].TrimStart('-', '/');
                var value = raw.Length > "settings".Length
                    ? raw["settings".Length..].TrimStart('=', ':')
                    : (i + 1 < args.Length ? args[++i] : "");
                if (value.Length > 0)
                {
                    SettingsFile = value;
                    Services.SettingsService.UseFile(value);
                }
            }
        }
    }
}
