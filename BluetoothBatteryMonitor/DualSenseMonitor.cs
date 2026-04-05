using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BluetoothBatteryMonitor
{
    internal sealed class DualSenseMonitor
    {
        // Windows HID selector for Sony controllers. The parser still focuses on PlayStation HID devices.
        private const string SonyHidSelector = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" AND System.DeviceInterface.Hid.VendorId:=1356";
        private const string GenericMonitorDisplayName = "Sony Controller Monitor";
        private static readonly TimeSpan BluetoothInferenceGracePeriod = TimeSpan.FromSeconds(5);
        private static readonly IControllerReportParser[] ReportParsers =
        [
            DualSenseReportParser.Instance,
            DualShock4ReportParser.Instance,
            FallbackSonyReportParser.Instance
        ];

        private PublishedMonitorStatus? _lastPublishedStatus;
        private readonly object _controllerOrderLock = new();
        private readonly List<string> _controllerDisplayOrder = [];
        private readonly Dictionary<string, ControllerStatus> _lastStatusesByControllerKey = new(StringComparer.OrdinalIgnoreCase);
        private string[] _lastVisibleControllerKeys = [];
        internal event Action<MonitorStatus>? StatusChanged;

        // Runs the main HID scan loop and publishes the best current status for each controller.
        internal async Task RunAsync(CancellationToken cancellationToken = default)
        {
            PublishStatus(MonitorStatus.Single(CreateStatus(
                ControllerStateKind.Scanning,
                displayName: GenericMonitorDisplayName,
                controllerFamily: ControllerFamily.UnknownSony,
                connectionType: null,
                isConnected: false,
                summaryText: "Scanning for controller...",
                detailText: null,
                batteryText: null,
                chargingText: null,
                deviceId: null)));

            // Rate-limit the access-denied screen so it does not flicker every loop.
            DateTime lastAccessDeniedAt = DateTime.MinValue;
            var controllerStates = new Dictionary<string, ControllerTrackingState>(StringComparer.OrdinalIgnoreCase);
            // Once the controller has been seen at least once, the "not found" message becomes "disconnected".
            bool hadConnectedController = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Re-enumerate HID interfaces each loop so power and transport changes are picked up quickly.
                    var devices = await DeviceInformation.FindAllAsync(SonyHidSelector);
                    var preferredDevices = GetPreferredDevices(devices);
                    if (preferredDevices.Length == 0)
                    {
                        controllerStates.Clear();
                        ResetControllerDisplayOrder();

                        string headline = hadConnectedController
                            ? "Controller disconnected."
                            : "Controller not found. It may be disconnected.";

                        PublishStatus(MonitorStatus.Single(CreateStatus(
                            ControllerStateKind.Disconnected,
                            displayName: GenericMonitorDisplayName,
                            controllerFamily: ControllerFamily.UnknownSony,
                            connectionType: null,
                            isConnected: false,
                            summaryText: headline,
                            detailText: "Turn on the controller or plug it in.",
                            batteryText: null,
                            chargingText: null,
                            deviceId: null)));

                        await DelayAsync(2000, cancellationToken);
                        continue;
                    }

                    var probeResult = await ProbeControllersAsync(preferredDevices);
                    if (probeResult.Snapshots.Length == 0)
                    {
                        if ((DateTime.Now - lastAccessDeniedAt).TotalSeconds >= 5)
                        {
                            // When no readable interface is found, surface the first few probe attempts so it is obvious
                            // whether Windows is returning stale paths, null handles, or real access failures.
                            string[] diagnosticLines = probeResult.Attempts
                                .Take(4)
                                .Select(attempt => $"{attempt.ConnectionType}: {attempt.Result}")
                                .ToArray();
                            PublishStatus(MonitorStatus.Single(CreateStatus(
                                ControllerStateKind.AccessDenied,
                                displayName: GenericMonitorDisplayName,
                                controllerFamily: ControllerFamily.UnknownSony,
                                connectionType: null,
                                isConnected: false,
                                summaryText: "Controller detected, but Windows denied HID access.",
                                detailText: "Close other apps that may own the controller, then wait.",
                                batteryText: null,
                                chargingText: null,
                                deviceId: null,
                                diagnostics: diagnosticLines)));
                            lastAccessDeniedAt = DateTime.Now;
                        }

                        await DelayAsync(2000, cancellationToken);
                        continue;
                    }

                    lastAccessDeniedAt = DateTime.MinValue;
                    MonitorStatus monitorStatus = BuildMonitorStatus(probeResult.Snapshots, controllerStates, ref hadConnectedController);
                    PublishStatus(monitorStatus);

                    await DelayAsync(1000, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Keep the monitor resilient to transient HID and enumeration failures.
                    PublishStatus(MonitorStatus.Single(CreateStatus(
                        ControllerStateKind.Error,
                        displayName: GenericMonitorDisplayName,
                        controllerFamily: ControllerFamily.UnknownSony,
                        connectionType: null,
                        isConnected: false,
                        summaryText: "Monitor error.",
                        detailText: ex.Message,
                        batteryText: null,
                        chargingText: null,
                        deviceId: null,
                        diagnostics: ["Retrying..."])));
                    await DelayAsync(2000, cancellationToken);
                }
            }
        }

        // Clears any remembered controller ordering when the monitor no longer sees live devices.
        private void ResetControllerDisplayOrder()
        {
            lock (_controllerOrderLock)
            {
                _controllerDisplayOrder.Clear();
                _lastStatusesByControllerKey.Clear();
                _lastVisibleControllerKeys = [];
            }
        }

        // Rotates the visible controller ordering so the tray and console can cycle the primary controller.
        internal void RotateControllerOrder()
        {
            MonitorStatus? rotatedStatus = null;

            lock (_controllerOrderLock)
            {
                if (_lastVisibleControllerKeys.Length < 2)
                {
                    return;
                }

                string[] rotatedVisibleKeys = _lastVisibleControllerKeys
                    .Skip(1)
                    .Concat(_lastVisibleControllerKeys.Take(1))
                    .ToArray();

                int[] indices = _lastVisibleControllerKeys
                    .Select(controllerKey => _controllerDisplayOrder.FindIndex(key => string.Equals(key, controllerKey, StringComparison.OrdinalIgnoreCase)))
                    .Where(index => index >= 0)
                    .OrderBy(index => index)
                    .ToArray();

                for (int index = 0; index < indices.Length && index < rotatedVisibleKeys.Length; index++)
                {
                    _controllerDisplayOrder[indices[index]] = rotatedVisibleKeys[index];
                }

                _lastVisibleControllerKeys = rotatedVisibleKeys;
                ControllerStatus[] orderedStatuses = rotatedVisibleKeys
                    .Select(controllerKey => _lastStatusesByControllerKey[controllerKey])
                    .ToArray();

                rotatedStatus = new MonitorStatus(orderedStatuses[0], orderedStatuses.Skip(1).ToArray());
            }

            PublishStatus(rotatedStatus.Value);
        }

        // Publishes a new status only when the controller state has materially changed.
        private void PublishStatus(MonitorStatus status)
        {
            PublishedMonitorStatus comparableStatus = PublishedMonitorStatus.From(status);
            if (_lastPublishedStatus == comparableStatus)
            {
                return;
            }

            _lastPublishedStatus = comparableStatus;
            StatusChanged?.Invoke(status);
        }

        // Builds the shared controller status shape used by the console app and the tray app.
        private static ControllerStatus CreateStatus(
            ControllerStateKind stateKind,
            string displayName,
            ControllerFamily controllerFamily,
            string? connectionType,
            bool isConnected,
            string summaryText,
            string? detailText,
            string? batteryText,
            string? chargingText,
            string? deviceId,
            string[]? diagnostics = null)
        {
            displayName = NormalizeDisplayName(displayName, controllerFamily);
            string tooltipText = BuildTooltipText(displayName, connectionType, isConnected, batteryText, chargingText, summaryText);

            return new ControllerStatus(
                stateKind,
                displayName,
                controllerFamily.ToDisplayName(),
                isConnected,
                connectionType,
                batteryText,
                chargingText,
                summaryText,
                detailText,
                deviceId,
                tooltipText,
                diagnostics ?? [],
                DateTimeOffset.Now);
        }

            // Normalizes Windows device names into stable labels for the UI.
        private static string NormalizeDisplayName(string displayName, ControllerFamily controllerFamily)
        {
            if (controllerFamily == ControllerFamily.DualSense)
            {
                return "DualSense Controller";
            }

            if (controllerFamily == ControllerFamily.DualShock4)
            {
                return "DualShock 4 Controller";
            }

            if (string.IsNullOrWhiteSpace(displayName) ||
                string.Equals(displayName, "Wireless Controller", StringComparison.OrdinalIgnoreCase))
            {
                return "Sony Controller";
            }

            return displayName;
        }

        // Produces the compact single-line summary used in tray hover text and other status surfaces.
        private static string BuildTooltipText(
            string displayName,
            string? connectionType,
            bool isConnected,
            string? batteryText,
            string? chargingText,
            string summaryText)
        {
            if (!isConnected)
            {
                return $"{displayName}: {summaryText}";
            }

            string transport = string.IsNullOrWhiteSpace(connectionType) ? "connected" : connectionType;
            string charge = string.IsNullOrWhiteSpace(chargingText) ? "status unknown" : chargingText;
            string battery = string.IsNullOrWhiteSpace(batteryText) ? "battery unknown" : batteryText;
            return $"{displayName} {transport}: {charge}, {battery}";
        }

        // Wraps Task.Delay so the monitor can honor cancellation consistently.
        private static Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            return cancellationToken.CanBeCanceled
                ? Task.Delay(milliseconds, cancellationToken)
                : Task.Delay(milliseconds);
        }

        // Sorts candidate HID interfaces so the most useful battery-reporting paths are probed first.
        private static DeviceInformation[] GetPreferredDevices(DeviceInformationCollection devices)
        {
            // Only enabled devices should count as connected; disabled entries are typically stale paths after disconnect.
            return devices
                .Where(device => device.IsEnabled)
                .OrderByDescending(device => IsPreferredUsbBatteryInterface(device.Id))
                .ThenByDescending(device => IsPreferredBluetoothBatteryInterface(device.Id))
                .ThenByDescending(device => IsPreferredBatteryInterface(device.Id))
                .ThenByDescending(device => GetConnectionType(device.Id) == "USB")
                .ThenByDescending(device => GetConnectionType(device.Id) == "Bluetooth")
                .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

            // Classifies a Windows HID path as USB, Bluetooth, or unknown using known Sony path patterns.
        private static string GetConnectionType(string deviceId)
        {
            // Windows exposes different HID path shapes for Bluetooth and USB. This classifier is heuristic,
            // but it is good enough to drive the parser selection and the UI.
            if (deviceId.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID&0002054c_PID&0ce6", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID&0002054c_PID&05c4", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID&0002054c_PID&09cc", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            {
                return "Bluetooth";
            }

            if (deviceId.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID_054C&PID_0CE6&MI_03", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("HID#VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID_054C&PID_05C4", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("HID#VID_054C&PID_05C4", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID_054C&PID_09CC", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("HID#VID_054C&PID_09CC", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("PID_0CE6", StringComparison.OrdinalIgnoreCase))
            {
                return "USB";
            }

            return "Unknown";
        }

        // Converts the raw probe snapshots into stable per-controller statuses and display order.
        private MonitorStatus BuildMonitorStatus(MonitorSnapshot[] snapshots, Dictionary<string, ControllerTrackingState> controllerStates, ref bool hadConnectedController)
        {
            LogicalControllerEntry[] logicalControllers = BuildLogicalControllers(snapshots);
            var currentControllerKeys = logicalControllers
                .Select(controller => controller.ControllerKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string controllerKey in currentControllerKeys)
            {
                if (!controllerStates.ContainsKey(controllerKey))
                {
                    controllerStates[controllerKey] = new ControllerTrackingState();
                }
            }

            foreach (string staleControllerKey in controllerStates.Keys.Except(currentControllerKeys, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                controllerStates.Remove(staleControllerKey);
            }

            var statusesByControllerKey = new Dictionary<string, ControllerStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (LogicalControllerEntry logicalController in logicalControllers)
            {
                statusesByControllerKey[logicalController.ControllerKey] = CreateStatusForSnapshot(
                    logicalController.Snapshot,
                    controllerStates[logicalController.ControllerKey],
                    ref hadConnectedController);
            }

            ControllerStatus[] orderedStatuses;
            lock (_controllerOrderLock)
            {
                _controllerDisplayOrder.RemoveAll(controllerKey => !statusesByControllerKey.ContainsKey(controllerKey));

                foreach (string controllerKey in statusesByControllerKey.Keys)
                {
                    if (!_controllerDisplayOrder.Contains(controllerKey, StringComparer.OrdinalIgnoreCase))
                    {
                        _controllerDisplayOrder.Add(controllerKey);
                    }
                }

                foreach (string staleStatusKey in _lastStatusesByControllerKey.Keys.Except(statusesByControllerKey.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    _lastStatusesByControllerKey.Remove(staleStatusKey);
                }

                foreach ((string controllerKey, ControllerStatus controllerStatus) in statusesByControllerKey)
                {
                    _lastStatusesByControllerKey[controllerKey] = controllerStatus;
                }

                string[] orderedControllerKeys = statusesByControllerKey.Keys
                    .OrderBy(controllerKey => _controllerDisplayOrder.FindIndex(key => string.Equals(key, controllerKey, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

                _lastVisibleControllerKeys = orderedControllerKeys;
                orderedStatuses = orderedControllerKeys
                    .Select(controllerKey => statusesByControllerKey[controllerKey])
                    .ToArray();
            }

            return new MonitorStatus(orderedStatuses[0], orderedStatuses.Skip(1).ToArray());
        }

        // Groups raw transport snapshots into logical controllers before the UI sees them.
        private static LogicalControllerEntry[] BuildLogicalControllers(MonitorSnapshot[] snapshots)
        {
            var logicalControllers = new List<LogicalControllerEntry>();
            bool[] consumedSnapshots = new bool[snapshots.Length];

            for (int index = 0; index < snapshots.Length; index++)
            {
                if (consumedSnapshots[index])
                {
                    continue;
                }

                MonitorSnapshot snapshot = snapshots[index];
                string rawControllerKey = GetControllerKey(snapshot.DeviceId);

                if (TryBuildMergedTransportController(snapshots, consumedSnapshots, index, out LogicalControllerEntry mergedController))
                {
                    logicalControllers.Add(mergedController);
                    continue;
                }

                consumedSnapshots[index] = true;
                logicalControllers.Add(new LogicalControllerEntry(rawControllerKey, snapshot));
            }

            return logicalControllers.ToArray();
        }

        // Merges matching USB and Bluetooth interfaces so one physical controller appears once in the UI.
        private static bool TryBuildMergedTransportController(
            MonitorSnapshot[] snapshots,
            bool[] consumedSnapshots,
            int index,
            out LogicalControllerEntry logicalController)
        {
            logicalController = default;

            MonitorSnapshot snapshot = snapshots[index];
            if (snapshot.ControllerFamily == ControllerFamily.UnknownSony)
            {
                return false;
            }

            if (snapshot.ConnectionType != "Bluetooth" && snapshot.ConnectionType != "USB")
            {
                return false;
            }

            int partnerIndex = -1;
            for (int candidateIndex = 0; candidateIndex < snapshots.Length; candidateIndex++)
            {
                if (candidateIndex == index || consumedSnapshots[candidateIndex])
                {
                    continue;
                }

                MonitorSnapshot candidate = snapshots[candidateIndex];
                if (candidate.ControllerFamily != snapshot.ControllerFamily)
                {
                    continue;
                }

                if (candidate.ConnectionType == snapshot.ConnectionType)
                {
                    continue;
                }

                if (!LooksLikeSameController(snapshot, candidate))
                {
                    continue;
                }

                partnerIndex = candidateIndex;
                break;
            }

            if (partnerIndex < 0)
            {
                return false;
            }

            MonitorSnapshot partner = snapshots[partnerIndex];
            MonitorSnapshot bluetoothSnapshot = snapshot.ConnectionType == "Bluetooth" ? snapshot : partner;
            MonitorSnapshot preferredSnapshot = PreferTransportSnapshot(snapshot, partner);

            consumedSnapshots[index] = true;
            consumedSnapshots[partnerIndex] = true;
            logicalController = new LogicalControllerEntry(GetControllerKey(bluetoothSnapshot.DeviceId), preferredSnapshot);
            return true;
        }

        // Uses battery and charge text to decide whether two snapshots likely represent the same controller.
        private static bool LooksLikeSameController(MonitorSnapshot first, MonitorSnapshot second)
        {
            if (first.ControllerFamily != second.ControllerFamily)
            {
                return false;
            }

            if (!string.Equals(first.BatteryText, second.BatteryText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(first.StatusText, second.StatusText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        // Chooses the better snapshot when multiple transport views exist for the same controller.
        private static MonitorSnapshot PreferTransportSnapshot(MonitorSnapshot first, MonitorSnapshot second)
        {
            if (first.ConnectionType == second.ConnectionType)
            {
                return ScoreSnapshot(first) >= ScoreSnapshot(second) ? first : second;
            }

            if (first.ConnectionType == "USB")
            {
                return first;
            }

            if (second.ConnectionType == "USB")
            {
                return second;
            }

            return ScoreSnapshot(first) >= ScoreSnapshot(second) ? first : second;
        }

        // Probes each candidate HID interface and keeps the best snapshot found for each controller key.
        private static async Task<ProbeResult> ProbeControllersAsync(DeviceInformation[] devices)
        {
            // Probe candidate interfaces in priority order and keep the best snapshot per controller.
            var attempts = new System.Collections.Generic.List<ProbeAttempt>();
            var bestSnapshotsByControllerKey = new Dictionary<string, MonitorSnapshot>(StringComparer.OrdinalIgnoreCase);
            var bestScoresByControllerKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                string connectionType = GetConnectionType(device.Id);
                var readAttempt = await TryOpenSnapshotAsync(device, connectionType, FileAccessMode.Read);
                if (readAttempt.Snapshot != null)
                {
                    var snapshot = readAttempt.Snapshot.Value;
                    attempts.Add(new ProbeAttempt(device.Name, connectionType, $"opened read {DescribeSnapshot(snapshot)} {ShortenDeviceId(device.Id)}"));
                    TryPromoteSnapshot(snapshot, bestSnapshotsByControllerKey, bestScoresByControllerKey);
                }

                attempts.Add(new ProbeAttempt(device.Name, connectionType, $"read {readAttempt.FailureReason ?? "null"} {ShortenDeviceId(device.Id)}"));

                var readWriteAttempt = await TryOpenSnapshotAsync(device, connectionType, FileAccessMode.ReadWrite);
                if (readWriteAttempt.Snapshot != null)
                {
                    var snapshot = readWriteAttempt.Snapshot.Value;
                    attempts.Add(new ProbeAttempt(device.Name, connectionType, $"opened readwrite {DescribeSnapshot(snapshot)} {ShortenDeviceId(device.Id)}"));
                    TryPromoteSnapshot(snapshot, bestSnapshotsByControllerKey, bestScoresByControllerKey);
                }

                attempts.Add(new ProbeAttempt(device.Name, connectionType, $"readwrite {readWriteAttempt.FailureReason ?? "null"} {ShortenDeviceId(device.Id)}"));
            }

            return new ProbeResult(bestSnapshotsByControllerKey.Values.ToArray(), attempts.ToArray());
        }

        // Tries to open one HID path and convert the best available report from it into a monitor snapshot.
        private static async Task<ProbeSnapshotAttempt> TryOpenSnapshotAsync(DeviceInformation device, string connectionType, FileAccessMode accessMode)
        {
            try
            {
                ControllerFamily controllerFamily = DetectControllerFamily(device.Id, report: null);

                // FromIdAsync returns a live HID handle when Windows allows access to that path.
                // HidDevice.FromIdAsync is the WinRT API that opens the actual HID interface.
                var hidDevice = await HidDevice.FromIdAsync(device.Id, accessMode);
                if (hidDevice == null)
                {
                    return new ProbeSnapshotAttempt(null, "returned null");
                }

                using (hidDevice)
                {
                    if (connectionType == "Bluetooth")
                    {
                        // Some controllers need a feature or output kick before Windows starts exposing their full BT report.
                        await TryEnableExtendedBluetoothReportsAsync(hidDevice, controllerFamily);
                    }

                    // Some controllers report immediately, others only after a short delay or after an input event.
                    // Capture whichever path returns first.
                    var capturedReport = await TryCaptureInputReportAsync(hidDevice);
                    if (capturedReport != null)
                    {
                        return new ProbeSnapshotAttempt(CreateSnapshot(device, connectionType, capturedReport.Value), null);
                    }

                    return new ProbeSnapshotAttempt(CreateSnapshot(device, connectionType, report: null), null);
                }
            }
            catch (Exception ex)
            {
                return new ProbeSnapshotAttempt(null, ex.GetType().Name);
            }
        }

        // Nudges Sony Bluetooth devices into exposing their richer battery-reporting input formats.
        private static async Task TryEnableExtendedBluetoothReportsAsync(HidDevice hidDevice, ControllerFamily controllerFamily)
        {
            if (controllerFamily == ControllerFamily.DualSense)
            {
                try
                {
                    // A feature-report read prompts DualSense to start emitting the extended Bluetooth input report 0x31,
                    // which includes battery and charging state. Without this, Windows often only exposes report 0x01.
                    // GetFeatureReportAsync asks the controller for a HID feature report without sending visible output.
                    await hidDevice.GetFeatureReportAsync(0x05).AsTask();
                }
                catch
                {
                    // Ignore failures here; the monitor will fall back to whatever report format the controller exposes.
                }

                return;
            }

            if (controllerFamily == ControllerFamily.DualShock4)
            {
                try
                {
                    // DS4 Bluetooth often needs a feature-report read before the full 0x11 report becomes available.
                    // GetFeatureReportAsync is used here as a compatibility nudge for DS4 Bluetooth report promotion.
                    await hidDevice.GetFeatureReportAsync(0x02).AsTask();
                }
                catch
                {
                }

                try
                {
                    // Some DS4 devices respond better when both known feature report IDs are queried.
                    await hidDevice.GetFeatureReportAsync(0x05).AsTask();
                }
                catch
                {
                }
            }
        }

        // Waits for either an event-driven or polled input report and returns whichever arrives first.
        private static async Task<ControllerInputReport?> TryCaptureInputReportAsync(HidDevice hidDevice)
        {
            // Combine event-driven reports with explicit polling so the monitor works for both chatty and quiet devices.
            var reportReceived = new TaskCompletionSource<ControllerInputReport?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Converts WinRT input-report events into the shared in-memory report shape.
            void OnInputReportReceived(HidDevice sender, HidInputReportReceivedEventArgs args)
            {
                reportReceived.TrySetResult(CreateCapturedInputReport(args.Report));
            }

            hidDevice.InputReportReceived += OnInputReportReceived;

            try
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    // GetInputReportAsync actively polls the HID device for a fresh report when no event has arrived yet.
                    var pollTask = hidDevice.GetInputReportAsync().AsTask();
                    var timeoutTask = Task.Delay(500);
                    var completedTask = await Task.WhenAny(pollTask, reportReceived.Task, timeoutTask);

                    if (completedTask == reportReceived.Task)
                    {
                        return reportReceived.Task.Result;
                    }

                    if (completedTask == pollTask && pollTask.IsCompletedSuccessfully && pollTask.Result != null)
                    {
                        return CreateCapturedInputReport(pollTask.Result);
                    }
                }

                var finalWait = await Task.WhenAny(reportReceived.Task, Task.Delay(800));
                if (finalWait == reportReceived.Task)
                {
                    return reportReceived.Task.Result;
                }

                return null;
            }
            finally
            {
                hidDevice.InputReportReceived -= OnInputReportReceived;
            }
        }

        // Copies the WinRT HID buffer into a plain byte array that the parsers can index directly.
        private static ControllerInputReport CreateCapturedInputReport(HidInputReport report)
        {
            // Copy the IBuffer into a byte[] so parsing logic can work with normal array indexes.
            byte[] bytes = new byte[report.Data.Length];
            // DataReader.FromBuffer bridges the WinRT IBuffer type into normal managed bytes.
            DataReader.FromBuffer(report.Data).ReadBytes(bytes);
            return new ControllerInputReport((byte)report.Id, bytes);
        }

        // Tells the probe ordering whether a HID path is likely to expose battery data.
        private static bool IsPreferredBatteryInterface(string deviceId)
        {
            return IsPreferredUsbBatteryInterface(deviceId) || IsPreferredBluetoothBatteryInterface(deviceId);
        }

        // Detects the preferred USB HID interface for known PlayStation controllers.
        private static bool IsPreferredUsbBatteryInterface(string deviceId)
        {
            // Prefer the main USB HID interface for known PlayStation controllers when Windows exposes it.
            return deviceId.Contains("MI_03", StringComparison.OrdinalIgnoreCase) &&
                (IsDualSenseDeviceId(deviceId) || IsDualShock4DeviceId(deviceId));
        }

        // Detects Bluetooth HID paths that usually carry the useful Sony battery report layout.
        private static bool IsPreferredBluetoothBatteryInterface(string deviceId)
        {
            // Bluetooth paths use the HID service GUID form instead of the USB MI_xx form.
            return (deviceId.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID&0002054c_PID&0ce6", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID&0002054c_PID&05c4", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("VID&0002054c_PID&09cc", StringComparison.OrdinalIgnoreCase)) &&
                (IsDualSenseDeviceId(deviceId) || IsDualShock4DeviceId(deviceId));
        }

        // Recognizes DualSense device IDs from the Windows HID path.
        private static bool IsDualSenseDeviceId(string deviceId)
        {
            return deviceId.Contains("PID_0CE6", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("PID&0ce6", StringComparison.OrdinalIgnoreCase);
        }

        // Recognizes DualShock 4 device IDs from the Windows HID path.
        private static bool IsDualShock4DeviceId(string deviceId)
        {
            return deviceId.Contains("PID_05C4", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("PID&05c4", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("PID_09CC", StringComparison.OrdinalIgnoreCase) ||
                deviceId.Contains("PID&09cc", StringComparison.OrdinalIgnoreCase);
        }

        // Infers the controller family from the device path first and the report ID as a fallback.
        private static ControllerFamily DetectControllerFamily(string deviceId, ControllerInputReport? report)
        {
            if (IsDualSenseDeviceId(deviceId))
            {
                return ControllerFamily.DualSense;
            }

            if (IsDualShock4DeviceId(deviceId))
            {
                return ControllerFamily.DualShock4;
            }

            if (report != null)
            {
                if (report.Value.ReportId == 0x31)
                {
                    return ControllerFamily.DualSense;
                }

                if (report.Value.ReportId == 0x11)
                {
                    return ControllerFamily.DualShock4;
                }
            }

            return ControllerFamily.UnknownSony;
        }

        // Replaces a stored snapshot only when the new candidate scores better for that controller.
        private static void TryPromoteSnapshot(
            MonitorSnapshot candidate,
            Dictionary<string, MonitorSnapshot> bestSnapshotsByControllerKey,
            Dictionary<string, int> bestScoresByControllerKey)
        {
            string controllerKey = GetControllerKey(candidate.DeviceId);
            int candidateScore = ScoreSnapshot(candidate);
            if (bestScoresByControllerKey.TryGetValue(controllerKey, out int existingScore) && candidateScore <= existingScore)
            {
                return;
            }

            bestSnapshotsByControllerKey[controllerKey] = candidate;
            bestScoresByControllerKey[controllerKey] = candidateScore;
        }

        // Scores snapshots so battery-capable and live-report interfaces win over weaker candidates.
        private static int ScoreSnapshot(MonitorSnapshot snapshot)
        {
            int score = 0;

            if (snapshot.HasBatteryData)
            {
                score += 100;
            }

            if (snapshot.HasLiveInputReport)
            {
                score += 10;
            }

            if (snapshot.ControllerFamily != ControllerFamily.UnknownSony)
            {
                score += 1;
            }

            return score;
        }

        // Produces a short textual summary of one probe result for diagnostics.
        private static string DescribeSnapshot(MonitorSnapshot snapshot)
        {
            if (snapshot.HasBatteryData)
            {
                return $"battery {snapshot.ControllerFamily.ToDisplayName()}";
            }

            if (snapshot.HasLiveInputReport)
            {
                return $"live {snapshot.ControllerFamily.ToDisplayName()} report 0x{snapshot.ReportId:X2}";
            }

            return $"quiet {snapshot.ControllerFamily.ToDisplayName()}";
        }

        // Converts one raw snapshot into the user-facing status while updating per-controller history.
        private ControllerStatus CreateStatusForSnapshot(MonitorSnapshot snapshot, ControllerTrackingState trackingState, ref bool hadConnectedController)
        {
            if (snapshot.HasBatteryData)
            {
                trackingState.InferOnBatteryForQuietBluetooth = false;
                trackingState.LastBatterySnapshot = snapshot;
                trackingState.LastBatterySnapshotAt = DateTime.Now;
                trackingState.HadConnectedController = true;
                hadConnectedController = true;

                ControllerStatus connectedStatus = CreateConnectedStatus(
                    snapshot,
                    inferredStatus: null,
                    batteryTextOverride: null,
                    rawHexOverride: null,
                    statusBytesOverride: null,
                    previousBluetoothSnapshot: trackingState.LastBluetoothSnapshot);

                if (snapshot.ConnectionType == "Bluetooth")
                {
                    trackingState.LastBluetoothSnapshot = snapshot;
                }

                trackingState.LastConnectionType = snapshot.ConnectionType;
                return connectedStatus;
            }

            bool hasRecentBatterySnapshot = trackingState.LastBatterySnapshot != null &&
                (DateTime.Now - trackingState.LastBatterySnapshotAt) <= BluetoothInferenceGracePeriod;

            if (snapshot.ConnectionType == "Bluetooth" &&
                string.Equals(trackingState.LastConnectionType, "USB", StringComparison.OrdinalIgnoreCase) &&
                hasRecentBatterySnapshot)
            {
                trackingState.InferOnBatteryForQuietBluetooth = true;
            }
            else if (snapshot.ConnectionType == "Bluetooth")
            {
                trackingState.InferOnBatteryForQuietBluetooth = false;
            }

            if (snapshot.ConnectionType != "Bluetooth")
            {
                trackingState.InferOnBatteryForQuietBluetooth = false;
            }

            ControllerStatus status = snapshot.ConnectionType == "Bluetooth" && !trackingState.InferOnBatteryForQuietBluetooth
                ? CreateDisconnectedStatus(snapshot)
                : CreatePendingStatus(snapshot, trackingState.LastBatterySnapshot, trackingState.InferOnBatteryForQuietBluetooth);

            if (status.IsConnected)
            {
                trackingState.HadConnectedController = true;
                hadConnectedController = true;
            }

            trackingState.LastConnectionType = snapshot.ConnectionType;
            return status;
        }

        // Builds a status for connected-but-incomplete devices, including the USB-to-Bluetooth handoff case.
        private ControllerStatus CreatePendingStatus(MonitorSnapshot snapshot, MonitorSnapshot? lastBatterySnapshot, bool inferOnBatteryForQuietBluetooth)
        {
            // If Bluetooth is connected but quiet, preserve the best available user-facing state rather than
            // oscillating between transport changes and "no report yet".
            if (snapshot.ConnectionType == "Bluetooth" && inferOnBatteryForQuietBluetooth && lastBatterySnapshot is MonitorSnapshot cachedSnapshot)
            {
                return CreateStatus(
                    ControllerStateKind.Connected,
                    displayName: snapshot.Name,
                    controllerFamily: snapshot.ControllerFamily,
                    connectionType: snapshot.ConnectionType,
                    isConnected: true,
                    summaryText: "Device detected and reporting.",
                    detailText: "Status inferred briefly during USB to Bluetooth handoff.",
                    batteryText: cachedSnapshot.BatteryText + " (last known)",
                    chargingText: "ON BATTERY 🔋 (transport inferred)",
                    deviceId: snapshot.DeviceId);
            }

            if (snapshot.ConnectionType == "Bluetooth" && snapshot.HasLiveInputReport && snapshot.ReportId == 0x01)
            {
                string detailText = snapshot.ControllerFamily switch
                {
                    ControllerFamily.DualSense => "Battery and charging data are unavailable until extended report 0x31 starts.",
                    ControllerFamily.DualShock4 => "Battery and charging data are unavailable until full report 0x11 starts.",
                    _ => "Battery and charging data are unavailable until the controller exposes a fuller report."
                };

                return CreateStatus(
                    ControllerStateKind.WaitingForBattery,
                    displayName: snapshot.Name,
                    controllerFamily: snapshot.ControllerFamily,
                    connectionType: snapshot.ConnectionType,
                    isConnected: true,
                    summaryText: "Connected with basic Bluetooth input report only.",
                    detailText: detailText,
                    batteryText: "unavailable",
                    chargingText: "unknown",
                    deviceId: snapshot.DeviceId,
                    diagnostics: ["Press a button, or close apps that may own the controller HID path."]);
            }

            return CreateStatus(
                ControllerStateKind.WaitingForBattery,
                displayName: snapshot.Name,
                controllerFamily: snapshot.ControllerFamily,
                connectionType: snapshot.ConnectionType,
                isConnected: true,
                summaryText: "Connected, but no battery report yet.",
                detailText: "Press a button if data does not appear.",
                batteryText: "unavailable",
                chargingText: "unknown",
                deviceId: snapshot.DeviceId);
        }

            // Builds the disconnected status shown when Windows still exposes a stale HID path.
        private static ControllerStatus CreateDisconnectedStatus(MonitorSnapshot snapshot)
        {
            return CreateStatus(
                ControllerStateKind.Disconnected,
                displayName: snapshot.Name,
                controllerFamily: snapshot.ControllerFamily,
                connectionType: snapshot.ConnectionType,
                isConnected: false,
                summaryText: "Controller appears disconnected.",
                detailText: "Windows still exposes a HID entry, but no live report was received.",
                batteryText: null,
                chargingText: null,
                deviceId: snapshot.DeviceId,
                diagnostics: ["Turn the controller on or reconnect it, then press a button."]);
        }

            // Builds the fully connected status shape from a snapshot with battery and charging information.
            private static ControllerStatus CreateConnectedStatus(
            MonitorSnapshot snapshot,
            string? inferredStatus,
            string? batteryTextOverride,
            byte? rawHexOverride,
            (byte SecondaryStatusByte, byte TertiaryStatusByte)? statusBytesOverride,
            MonitorSnapshot? previousBluetoothSnapshot)
        {
            // Rendering is centralized so the inferred-state path and real-report path stay visually consistent.
            string statusText = inferredStatus ?? snapshot.StatusText ?? "UNKNOWN";
            string batteryText = batteryTextOverride ?? snapshot.BatteryText ?? "unknown";
            byte primaryStatusByte = rawHexOverride ?? snapshot.PrimaryStatusByte;
            byte secondaryStatusByte = statusBytesOverride?.SecondaryStatusByte ?? snapshot.SecondaryStatusByte;
            byte tertiaryStatusByte = statusBytesOverride?.TertiaryStatusByte ?? snapshot.TertiaryStatusByte;
            // Keep computing this so the helper stays easy to re-enable during future Bluetooth debugging.
            _ = BuildBluetoothDebugLine(snapshot, previousBluetoothSnapshot, primaryStatusByte, secondaryStatusByte, tertiaryStatusByte);

            return CreateStatus(
                ControllerStateKind.Connected,
                displayName: snapshot.Name,
                controllerFamily: snapshot.ControllerFamily,
                connectionType: snapshot.ConnectionType,
                isConnected: true,
                summaryText: snapshot.HasBatteryData ? "Device detected and reporting." : "Connected without a fresh battery report.",
                detailText: null,
                batteryText: batteryText,
                chargingText: statusText,
                deviceId: snapshot.DeviceId
                // Debug lines kept in place for future troubleshooting.
                // , diagnostics:
                // [
                //     $"Raw Hex: 0x{primaryStatusByte:X2}",
                //     $"Report: 0x{snapshot.ReportId:X2} ({snapshot.ReportLength} bytes)",
                //     $"USB Probe: {snapshot.UsbProbeWindow}",
                //     $"BT Probe:  {snapshot.BluetoothProbeWindow}",
                //     bluetoothDebugLine,
                //     $"Bytes:   0x{secondaryStatusByte:X2} | 0x{tertiaryStatusByte:X2}"
                // ]
                );
        }

            // Computes a compact Bluetooth byte-delta string used during parser debugging.
        private static string BuildBluetoothDebugLine(
            MonitorSnapshot snapshot,
            MonitorSnapshot? previousBluetoothSnapshot,
            byte primaryStatusByte,
            byte secondaryStatusByte,
            byte tertiaryStatusByte)
        {
            if (snapshot.ConnectionType != "Bluetooth")
            {
                return "BT Delta: n/a";
            }

            if (previousBluetoothSnapshot is not MonitorSnapshot previousSnapshot)
            {
                return "BT Delta: initial sample";
            }

            var changes = new System.Collections.Generic.List<string>();

            if (previousSnapshot.PrimaryStatusByte != primaryStatusByte)
            {
                changes.Add($"raw 0x{previousSnapshot.PrimaryStatusByte:X2}->0x{primaryStatusByte:X2}");
            }

            if (previousSnapshot.SecondaryStatusByte != secondaryStatusByte)
            {
                changes.Add($"b53 0x{previousSnapshot.SecondaryStatusByte:X2}->0x{secondaryStatusByte:X2}");
            }

            if (previousSnapshot.TertiaryStatusByte != tertiaryStatusByte)
            {
                changes.Add($"b55 0x{previousSnapshot.TertiaryStatusByte:X2}->0x{tertiaryStatusByte:X2}");
            }

            if (!string.Equals(previousSnapshot.BluetoothProbeWindow, snapshot.BluetoothProbeWindow, StringComparison.Ordinal))
            {
                changes.Add("probe changed");
            }

            return changes.Count == 0
                ? "BT Delta: none"
                : $"BT Delta: {string.Join(", ", changes)}";
        }

        // Converts a device plus optional report into the normalized monitor snapshot used everywhere else.
        private static MonitorSnapshot CreateSnapshot(DeviceInformation device, string connectionType, ControllerInputReport? report)
        {
            ControllerFamily controllerFamily = DetectControllerFamily(device.Id, report);

            if (report == null)
            {
                // No input report means we detected the HID path but got no live payload from it.
                return new MonitorSnapshot(device.Name, controllerFamily, device.Id, connectionType, false, false, 0, 0, null, null, 0, 0, 0, "n/a", "n/a");
            }

            byte[] bytes = report.Value.Data;
            byte reportId = report.Value.ReportId;

            // Report type matters for battery parsing, especially on Bluetooth where report 0x01 has no battery data.
            string resolvedConnectionType = connectionType;
            if (resolvedConnectionType == "Unknown")
            {
                // If the path was ambiguous, infer the transport from the report style and size.
                resolvedConnectionType = reportId == 0x31 ? "Bluetooth" : bytes.Length > 16 ? "USB" : "Bluetooth";
            }

            ControllerReportParseResult parseResult = ParseReport(controllerFamily, resolvedConnectionType, report.Value);

            return new MonitorSnapshot(
                device.Name,
                controllerFamily,
                device.Id,
                resolvedConnectionType,
                parseResult.HasBatteryData,
                true,
                reportId,
                bytes.Length,
                parseResult.StatusText,
                parseResult.BatteryText,
                parseResult.PrimaryStatusByte,
                parseResult.SecondaryStatusByte,
                parseResult.TertiaryStatusByte,
                parseResult.UsbProbeWindow,
                parseResult.BluetoothProbeWindow);
        }

        // Chooses the correct parser implementation for the current controller family and transport.
        private static ControllerReportParseResult ParseReport(ControllerFamily controllerFamily, string connectionType, ControllerInputReport report)
        {
            IControllerReportParser parser = ReportParsers.First(candidate => candidate.CanParse(controllerFamily));
            return parser.Parse(connectionType, report);
        }

        // Normalizes a HID path down to a stable logical-controller key across interface variants.
        private static string GetControllerKey(string deviceId)
        {
            const string interfaceMarker = "&MI_";
            int markerIndex = deviceId.IndexOf(interfaceMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0 || markerIndex + 6 > deviceId.Length)
            {
                return deviceId;
            }

            return deviceId.Remove(markerIndex, 6);
        }

        private readonly record struct MonitorSnapshot(
            string Name,
            ControllerFamily ControllerFamily,
            string DeviceId,
            string ConnectionType,
            bool HasBatteryData,
            bool HasLiveInputReport,
            byte ReportId,
            int ReportLength,
            string? StatusText,
            string? BatteryText,
            byte PrimaryStatusByte,
            byte SecondaryStatusByte,
            byte TertiaryStatusByte,
            string UsbProbeWindow,
            string BluetoothProbeWindow);

        private readonly record struct LogicalControllerEntry(
            string ControllerKey,
            MonitorSnapshot Snapshot);

        private readonly record struct ProbeAttempt(
            string Name,
            string ConnectionType,
            string Result);

        private readonly record struct ProbeSnapshotAttempt(
            MonitorSnapshot? Snapshot,
            string? FailureReason);

        private readonly record struct ProbeResult(
            MonitorSnapshot[] Snapshots,
            ProbeAttempt[] Attempts);

        private sealed class ControllerTrackingState
        {
            internal MonitorSnapshot? LastBatterySnapshot { get; set; }

            internal MonitorSnapshot? LastBluetoothSnapshot { get; set; }

            internal DateTime LastBatterySnapshotAt { get; set; }

            internal string? LastConnectionType { get; set; }

            internal bool InferOnBatteryForQuietBluetooth { get; set; }

            internal bool HadConnectedController { get; set; }
        }

        private readonly record struct PublishedStatus(
            ControllerStateKind StateKind,
            string DisplayName,
            string DeviceKind,
            bool IsConnected,
            string? ConnectionType,
            string? BatteryText,
            string? ChargingText,
            string SummaryText,
            string? DetailText,
            string? DeviceId,
            string TooltipText,
            string DiagnosticsKey)
        {
            // Converts a controller status into a comparison-friendly form for change detection.
            internal static PublishedStatus From(ControllerStatus status)
            {
                return new PublishedStatus(
                    status.StateKind,
                    status.DisplayName,
                    status.DeviceKind,
                    status.IsConnected,
                    status.ConnectionType,
                    status.BatteryText,
                    status.ChargingText,
                    status.SummaryText,
                    status.DetailText,
                    status.DeviceId,
                    status.TooltipText,
                    string.Join("\u001F", status.Diagnostics));
            }

            // Flattens the status into a stable key so repeated identical updates can be suppressed.
            internal string ToStableString()
            {
                return string.Join(
                    "\u001F",
                    StateKind,
                    DisplayName,
                    DeviceKind,
                    IsConnected,
                    ConnectionType,
                    BatteryText,
                    ChargingText,
                    SummaryText,
                    DetailText,
                    DeviceId,
                    TooltipText,
                    DiagnosticsKey);
            }
        }

        private readonly record struct PublishedMonitorStatus(
            PublishedStatus PrimaryStatus,
            string OtherControllersKey)
        {
            // Converts the full monitor status into the comparison shape used by PublishStatus.
            internal static PublishedMonitorStatus From(MonitorStatus status)
            {
                string otherControllersKey = string.Join(
                    "\u001E",
                    status.OtherControllers
                        .Select(PublishedStatus.From)
                        .Select(controller => controller.ToStableString()));

                return new PublishedMonitorStatus(
                    PublishedStatus.From(status.PrimaryController),
                    otherControllersKey);
            }
        }

        // Shortens long HID paths so probe diagnostics stay readable in the console and details view.
        private static string ShortenDeviceId(string deviceId)
        {
            const int maxLength = 42;
            if (deviceId.Length <= maxLength)
            {
                return deviceId;
            }

            return deviceId[..maxLength] + "...";
        }
    }
}