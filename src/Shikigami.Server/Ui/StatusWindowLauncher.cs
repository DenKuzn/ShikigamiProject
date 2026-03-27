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
    public static void Start(ShikigamiState state)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var window = new StatusWindow(state);
                window.Show();
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
}
