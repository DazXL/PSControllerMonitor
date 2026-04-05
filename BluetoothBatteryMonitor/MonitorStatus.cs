namespace BluetoothBatteryMonitor
{
    internal readonly record struct MonitorStatus(
        ControllerStatus PrimaryController,
        ControllerStatus[] OtherControllers)
    {
        // Wraps a single controller status into the standard multi-controller shape.
        internal static MonitorStatus Single(ControllerStatus status)
        {
            return new MonitorStatus(status, []);
        }
    }
}