using System;
using System.Linq;

namespace BluetoothBatteryMonitor
{
    internal sealed class DualShock4ReportParser : IControllerReportParser
    {
        internal static readonly DualShock4ReportParser Instance = new();

        // Keeps construction internal to the shared singleton instance.
        private DualShock4ReportParser()
        {
        }

        // Reports whether this parser should be used for DualShock 4 devices.
        public bool CanParse(ControllerFamily controllerFamily)
        {
            return controllerFamily == ControllerFamily.DualShock4;
        }

        // Reads the DualShock 4 battery and charge bytes from USB or Bluetooth report layouts.
        public ControllerReportParseResult Parse(string connectionType, ControllerInputReport report)
        {
            byte[] bytes = report.Data;
            byte reportId = report.ReportId;

            int status0Index;
            int status1Index;

            if (connectionType == "USB" && reportId == 0x01 && bytes.Length > 30)
            {
                status0Index = 30;
                status1Index = 31;
            }
            else if (connectionType == "Bluetooth" && reportId == 0x11 && bytes.Length > 32)
            {
                status0Index = 32;
                status1Index = 33;
            }
            else
            {
                return new ControllerReportParseResult(
                    false,
                    null,
                    null,
                    0,
                    0,
                    0,
                    BuildProbeWindow(bytes, 20, 34),
                    BuildProbeWindow(bytes, 28, 36));
            }

            byte status0 = bytes[status0Index];
            byte status1 = bytes.Length > status1Index ? bytes[status1Index] : (byte)0;
            bool cableConnected = (status0 & 0x10) != 0;
            int batteryData = status0 & 0x0F;

            string statusText;
            string batteryText;

            if (cableConnected)
            {
                if (batteryData < 10)
                {
                    statusText = "CHARGING ⚡";
                    batteryText = FormatBatteryLevelRange(batteryData);
                }
                else if (batteryData == 10)
                {
                    statusText = "CHARGING ⚡";
                    batteryText = "100%";
                }
                else if (batteryData == 11)
                {
                    statusText = "FULLY CHARGED ✅";
                    batteryText = "100%";
                }
                else if (batteryData == 14)
                {
                    statusText = "NOT CHARGING ⚠";
                    batteryText = "unknown";
                }
                else
                {
                    statusText = "CHARGE ERROR ⚠";
                    batteryText = "unknown";
                }
            }
            else
            {
                statusText = "ON BATTERY 🔋";
                batteryText = batteryData >= 10
                    ? "100%"
                    : FormatBatteryLevelRange(batteryData);
            }

            return new ControllerReportParseResult(
                true,
                statusText,
                batteryText,
                status0,
                status1,
                0,
                BuildProbeWindow(bytes, 20, 34),
                BuildProbeWindow(bytes, 28, 36));
        }

        // Converts the DS4 battery nibble into the percentage range shown in the UI.
        private static string FormatBatteryLevelRange(int level)
        {
            level = Math.Clamp(level, 0, 9);
            int lowerBound = level * 10;
            int upperBound = lowerBound + 9;
            return $"{lowerBound}-{upperBound}%";
        }

        // Formats a small byte window so unknown report layouts are easier to inspect.
        private static string BuildProbeWindow(byte[] bytes, int start, int end)
        {
            if (bytes.Length == 0 || start >= bytes.Length)
            {
                return "unavailable";
            }

            int clampedStart = Math.Max(0, start);
            int clampedEnd = Math.Min(end, bytes.Length - 1);
            return string.Join(" ", Enumerable.Range(clampedStart, clampedEnd - clampedStart + 1)
                .Select(index => $"{index}:{bytes[index]:X2}"));
        }
    }
}