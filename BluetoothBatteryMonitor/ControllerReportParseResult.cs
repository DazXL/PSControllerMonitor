namespace BluetoothBatteryMonitor
{
    // Holds the battery and charge fields extracted from one controller input report.
    internal readonly record struct ControllerReportParseResult(
        bool HasBatteryData,
        string? StatusText,
        string? BatteryText,
        byte PrimaryStatusByte,
        byte SecondaryStatusByte,
        byte TertiaryStatusByte,
        string UsbProbeWindow,
        string BluetoothProbeWindow);
}