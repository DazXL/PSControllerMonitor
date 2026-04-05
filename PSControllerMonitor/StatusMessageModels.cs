using System.Text.Json.Serialization;

namespace PSControllerMonitor;

internal static class StatusMessageModels
{
    internal const string PipeName = "BluetoothBatteryMonitor.Status";
    internal const string CommandPipeName = "BluetoothBatteryMonitor.Commands";
    internal const string MessageType = "controller-status";
    internal const int Version = 1;
    internal const string RotateControllersCommand = "rotate-controller-order";
}

// Represents the top-level JSON envelope read from the monitor status pipe.
internal sealed class StatusEnvelope
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("status")]
    public StatusPayload? Status { get; init; }
}

// Represents one controller-status payload after JSON deserialization.
internal sealed class StatusPayload
{
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("deviceKind")]
    public string? DeviceKind { get; init; }

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; init; }

    [JsonPropertyName("connectionType")]
    public string? ConnectionType { get; init; }

    [JsonPropertyName("batteryText")]
    public string? BatteryText { get; init; }

    [JsonPropertyName("chargingText")]
    public string? ChargingText { get; init; }

    [JsonPropertyName("summaryText")]
    public string? SummaryText { get; init; }

    [JsonPropertyName("detailText")]
    public string? DetailText { get; init; }

    [JsonPropertyName("tooltipText")]
    public string? TooltipText { get; init; }

    [JsonPropertyName("diagnostics")]
    public string[] Diagnostics { get; init; } = [];

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTime LastUpdatedUtc { get; init; }

    [JsonPropertyName("otherControllers")]
    public StatusPayload[] OtherControllers { get; init; } = [];
}