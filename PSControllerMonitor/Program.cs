using BluetoothBatteryMonitor;

namespace PSControllerMonitor;

internal static class Program
{
    [STAThread]
    // Initializes WinForms, starts the embedded monitor host, and enters the tray message loop.
    private static async Task Main()
    {
        // ApplicationConfiguration.Initialize applies the standard WinForms defaults for DPI, fonts, and visuals.
        ApplicationConfiguration.Initialize();

        await using var monitorHost = new MonitorHost();
        monitorHost.Start();

        using var trayApplicationContext = new TrayApplicationContext();
        Application.Run(trayApplicationContext);
    }
}