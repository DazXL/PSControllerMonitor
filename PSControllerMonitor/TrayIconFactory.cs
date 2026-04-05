using System.Drawing;
using System.Globalization;
using System.Reflection;

namespace PSControllerMonitor;

internal static class TrayIconFactory
{
    private static readonly Icon FoundConnectedIcon = LoadIcon("32connected.ico");
    private static readonly Icon DisconnectedIcon = LoadIcon("32notfound.ico");
    private static readonly Icon BluetoothChargingIcon = LoadIcon("BTchgn.ico");
    private static readonly Icon BluetoothIcon = LoadIcon("BTcnx.ico");
    private static readonly Icon BluetoothFullChargeIcon = LoadIcon("BTfullch.ico");
    private static readonly Icon BluetoothLowIcon = LoadIcon("BTlow.ico");
    private static readonly Icon UsbIcon = LoadIcon("USBcnx.ico");

    // Clones the resolved tray icon so the caller can dispose it without touching shared resources.
    internal static Icon Create(StatusPayload status, bool showFoundConnected = false)
    {
        Icon source = ResolveIcon(status, showFoundConnected);
        return (Icon)source.Clone();
    }

    // Chooses the tray icon that best matches the aggregate controller state.
    private static Icon ResolveIcon(StatusPayload status, bool showFoundConnected)
    {
        if (!status.IsConnected)
        {
            return DisconnectedIcon;
        }

        if (showFoundConnected)
        {
            return FoundConnectedIcon;
        }

        IReadOnlyList<StatusPayload> controllers = GetConnectedControllers(status);
        bool anyBluetooth = controllers.Any(controller => string.Equals(controller.ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase));
        bool allUsb = controllers.Count > 0 && controllers.All(controller => string.Equals(controller.ConnectionType, "USB", StringComparison.OrdinalIgnoreCase));

        if (controllers.Any(controller =>
            string.Equals(controller.ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase) &&
            (controller.ChargingText ?? string.Empty).Contains("CHARGING", StringComparison.OrdinalIgnoreCase)))
        {
            return BluetoothChargingIcon;
        }

        if (controllers.Any(controller =>
            string.Equals(controller.ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase) &&
            (controller.ChargingText ?? string.Empty).Contains("FULLY CHARGED", StringComparison.OrdinalIgnoreCase)))
        {
            return BluetoothFullChargeIcon;
        }

        if (controllers.Any(controller =>
            string.Equals(controller.ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase) &&
            IsLowBluetoothBattery(controller.BatteryText)))
        {
            return BluetoothLowIcon;
        }

        if (allUsb)
        {
            return UsbIcon;
        }

        if (anyBluetooth)
        {
            return BluetoothIcon;
        }

        return DisconnectedIcon;
    }

    // Collects the primary and additional connected controllers into one list for icon decisions.
    private static IReadOnlyList<StatusPayload> GetConnectedControllers(StatusPayload status)
    {
        if (!status.IsConnected)
        {
            return [];
        }

        StatusPayload[] otherControllers = status.OtherControllers ?? [];

        var controllers = new List<StatusPayload>(1 + otherControllers.Length)
        {
            status
        };
        controllers.AddRange(otherControllers.Where(otherController => otherController?.IsConnected == true));
        return controllers;
    }

    // Detects whether a Bluetooth battery string represents a low-charge state.
    private static bool IsLowBluetoothBattery(string? batteryText)
    {
        if (string.IsNullOrWhiteSpace(batteryText))
        {
            return false;
        }

        string normalized = batteryText.Trim();
        int percentIndex = normalized.IndexOf('%');
        if (percentIndex >= 0)
        {
            normalized = normalized[..percentIndex];
        }

        int rangeSeparatorIndex = normalized.IndexOf('-');
        if (rangeSeparatorIndex >= 0)
        {
            string lowerBoundText = normalized[..rangeSeparatorIndex].Trim();
            return int.TryParse(lowerBoundText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lowerBound)
                && lowerBound < 15;
        }

        return int.TryParse(normalized.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int exactPercent)
            && exactPercent < 15;
    }

    // Loads an embedded tray icon resource from the published assembly.
    private static Icon LoadIcon(string fileName)
    {
        Assembly assembly = typeof(TrayIconFactory).Assembly;
        string resourceName = $"{typeof(TrayIconFactory).Namespace}.icons.{fileName}";
        // GetManifestResourceStream opens the icon bytes that were embedded for single-file publishing.
        Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Tray icon resource was not found: {resourceName}", resourceName);
        }

        using (stream)
        {
            return new Icon(stream);
        }
    }
}