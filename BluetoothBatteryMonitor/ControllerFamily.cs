namespace BluetoothBatteryMonitor
{
    internal enum ControllerFamily
    {
        UnknownSony,
        DualSense,
        DualShock4
    }

    internal static class ControllerFamilyExtensions
    {
        // Converts the internal controller family enum into the UI device label.
        internal static string ToDisplayName(this ControllerFamily controllerFamily)
        {
            return controllerFamily switch
            {
                ControllerFamily.DualSense => "DualSense",
                ControllerFamily.DualShock4 => "DualShock 4",
                _ => "Sony Controller"
            };
        }
    }
}