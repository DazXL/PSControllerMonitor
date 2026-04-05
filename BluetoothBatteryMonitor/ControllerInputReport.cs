namespace BluetoothBatteryMonitor
{
    // Carries one raw HID input report after it has been copied into normal managed bytes.
    internal readonly record struct ControllerInputReport(
        byte ReportId,
        byte[] Data);
}