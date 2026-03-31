using System.Windows;
using System.Windows.Threading;
using Shikigami.Core.State;

namespace Shikigami.Server.Ui;

/// <summary>
/// Launches the StatusWindow in a separate STA thread.
/// Fire-and-forget — matches the Python daemon thread pattern.
/// </summary>
public static class StatusWindowLauncher
{
    private static Dispatcher? _uiDispatcher;

    public static void Start(ShikigamiState state)
    {
        var settings = ServerSettings.Load();

        var thread = new Thread(() =>
        {
            try
            {
                _uiDispatcher = Dispatcher.CurrentDispatcher;
                var window = new StatusWindow(state, settings);
                if (settings.ShowWindowOnStartup)
                    window.Show();
                else
                    window.HideToTray();
                Dispatcher.Run();
            }
            catch
            {
                // Fire-and-forget — don't crash the server if UI fails
            }
        })
        {
            IsBackground = true,
            Name = "ShikigamiStatusWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Gracefully shut down the UI dispatcher so tray icon is disposed.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            _uiDispatcher?.InvokeShutdown();
        }
        catch
        {
            // Already shut down
        }
    }
}
