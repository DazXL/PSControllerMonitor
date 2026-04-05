using System;
using System.Linq;

namespace BluetoothBatteryMonitor
{
    internal sealed class DualSenseReportParser : IControllerReportParser
    {
        internal static readonly DualSenseReportParser Instance = new();

        // Keeps construction internal to the shared singleton instance.
        private DualSenseReportParser()
        {
        }

        // Reports whether this parser should be used for DualSense devices.
        public bool CanParse(ControllerFamily controllerFamily)
        {
            return controllerFamily == ControllerFamily.DualSense;
        }

        // Reads the DualSense battery and charge bytes from USB or Bluetooth report layouts.
        public ControllerReportParseResult Parse(string connectionType, ControllerInputReport report)
        {
            byte[] bytes = report.Data;
            byte reportId = report.ReportId;

            int status0Index;
            int status1Index;

            if (connectionType == "USB" && reportId == 0x01 && bytes.Length > 53)
            {
                status0Index = 53;
                status1Index = 54;
            }
            else if (connectionType == "Bluetooth" && reportId == 0x31 && bytes.Length > 54)
            {
                status0Index = 54;
                status1Index = 55;
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
                    BuildProbeWindow(bytes, 28, 36),
                    BuildProbeWindow(bytes, 50, 56));
            }

            byte status0 = bytes[status0Index];
            byte status1 = bytes.Length > status1Index ? bytes[status1Index] : (byte)0;
            int batteryLevel = status0 & 0x0F;
            int chargingStatus = (status0 & 0xF0) >> 4;

            string statusText = chargingStatus switch
            {
                0x0 => "ON BATTERY 🔋",
                0x1 => "CHARGING ⚡",
                0x2 => "FULLY CHARGED ✅",
                0xA => "NOT CHARGING ⚠",
                0xB => "NOT CHARGING ⚠",
                0xF => "CHARGE ERROR ⚠",
                _ => $"UNKNOWN (0x{chargingStatus:X})"
            };

            string batteryText = chargingStatus == 0x2
                ? "100%"
                : FormatBatteryLevel(batteryLevel);

            return new ControllerReportParseResult(
                true,
                statusText,
                batteryText,
                status0,
                status1,
                0,
                BuildProbeWindow(bytes, 28, 36),
                BuildProbeWindow(bytes, 50, 56));
        }

            // Converts Sony's 0-10 battery scale into the percentage string shown in the UI.
        private static string FormatBatteryLevel(int level)
        {
            level = Math.Clamp(level, 0, 10);
            if (level >= 10)
            {
                return "100%";
            }

            int lowerBound = level * 10;
            int upperBound = Math.Min(lowerBound + 9, 99);
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