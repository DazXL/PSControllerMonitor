namespace BluetoothBatteryMonitor
{
    internal interface IControllerReportParser
    {
        // Tells the monitor whether this parser handles the given controller family.
        bool CanParse(ControllerFamily controllerFamily);

        // Extracts battery and charging data from a controller input report.
        ControllerReportParseResult Parse(string connectionType, ControllerInputReport report);
    }
}