using System;

namespace BluetoothBatteryMonitor
{
    internal enum ControllerStateKind
    {
        Scanning,
        Connected,
        WaitingForBattery,
        Disconnected,
        AccessDenied,
        Error
    }

    // Represents the normalized controller state that the console and tray app both consume.
    internal readonly record struct ControllerStatus(
        ControllerStateKind StateKind,
        string DisplayName,
        string DeviceKind,
        bool IsConnected,
        string? ConnectionType,
        string? BatteryText,
        string? ChargingText,
        string SummaryText,
        string? DetailText,
        string? DeviceId,
        string TooltipText,
        string[] Diagnostics,
        DateTimeOffset LastUpdated);
}