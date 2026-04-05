using System;
using System.Linq;

namespace BluetoothBatteryMonitor
{
    internal sealed class FallbackSonyReportParser : IControllerReportParser
    {
        internal static readonly FallbackSonyReportParser Instance = new();

        // Keeps construction internal to the shared singleton instance.
        private FallbackSonyReportParser()
        {
        }

        // Accepts any Sony-family device that did not match a more specific parser.
        public bool CanParse(ControllerFamily controllerFamily)
        {
            return true;
        }

        // Returns probe windows only, because unknown layouts are not decoded into battery data.
        public ControllerReportParseResult Parse(string connectionType, ControllerInputReport report)
        {
            byte[] bytes = report.Data;

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

            // Formats a small byte window so fallback reports still carry useful diagnostics.
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