using System;

namespace BluetoothBatteryMonitor
{
    internal static class StatusMessage
    {
        internal const string PipeName = "BluetoothBatteryMonitor.Status";
        internal const string CommandPipeName = "BluetoothBatteryMonitor.Commands";
        internal const string MessageType = "controller-status";
        internal const int Version = 1;
        internal const string RotateControllersCommand = "rotate-controller-order";

        // Converts the in-memory monitor state into the stable JSON message sent over the status pipe.
        internal static StatusEnvelope FromStatus(MonitorStatus status)
        {
            StatusPayload[] otherControllers = status.OtherControllers
                .Select(controller => ToPayload(controller, otherControllers: null))
                .ToArray();

            return new StatusEnvelope(
                MessageType,
                Version,
                ToPayload(status.PrimaryController, otherControllers));
        }

            // Maps a single controller status into the serialized payload shape.
        private static StatusPayload ToPayload(ControllerStatus status, StatusPayload[]? otherControllers)
        {
            return new StatusPayload(
                State: status.StateKind.ToString(),
                DisplayName: status.DisplayName,
                DeviceKind: status.DeviceKind,
                IsConnected: status.IsConnected,
                ConnectionType: status.ConnectionType,
                BatteryText: status.BatteryText,
                ChargingText: status.ChargingText,
                SummaryText: status.SummaryText,
                DetailText: status.DetailText,
                TooltipText: status.TooltipText,
                Diagnostics: status.Diagnostics,
                LastUpdatedUtc: status.LastUpdated.UtcDateTime,
                OtherControllers: otherControllers);
        }
    }

    internal readonly record struct StatusEnvelope(
        string Type,
        int Version,
        StatusPayload Status);

    internal readonly record struct StatusPayload(
        string State,
        string DisplayName,
        string DeviceKind,
        bool IsConnected,
        string? ConnectionType,
        string? BatteryText,
        string? ChargingText,
        string SummaryText,
        string? DetailText,
        string TooltipText,
        string[] Diagnostics,
        DateTime LastUpdatedUtc,
        StatusPayload[]? OtherControllers);
}