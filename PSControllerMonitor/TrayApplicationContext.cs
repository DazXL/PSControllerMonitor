using System.Drawing;
using System.Text;

namespace PSControllerMonitor;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly bool UseCustomHoverPanel = true;
    private static readonly Size TrayHoverHitSize = new(44, 44);

    private readonly SynchronizationContext? _uiContext;
    private readonly ContextMenuStrip _contextMenu;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _reorderControllersMenuItem;
    private readonly ToolStripMenuItem _showDetailsMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;
    private readonly System.Windows.Forms.Timer _foundConnectedTimer;
    private readonly System.Windows.Forms.Timer _hoverShowTimer;
    private readonly System.Windows.Forms.Timer _hoverRotateTimer;
    private readonly System.Windows.Forms.Timer _hoverWatchTimer;
    private readonly NamedPipeStatusClient _pipeClient;
    private readonly NamedPipeCommandClient _commandClient;
    private readonly StatusDetailsForm _detailsForm;
    private readonly HoverStatusForm _hoverStatusForm;
    private readonly List<ToolStripMenuItem> _controllerMenuItems = [];

    private StatusPayload _currentStatus;
    private bool _isExiting;
    private bool _cleanupCompleted;
    private Point _lastTrayHoverPoint;
    private int _hoverControllerIndex;

    // Builds the tray icon, hover UI, details form, timers, and pipe clients for the application lifetime.
    internal TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current;
        _currentStatus = CreateWaitingStatus("Waiting for monitor...");
        _pipeClient = new NamedPipeStatusClient(StatusMessageModels.PipeName);
        _commandClient = new NamedPipeCommandClient(StatusMessageModels.CommandPipeName);
        _detailsForm = new StatusDetailsForm();
        _hoverStatusForm = new HoverStatusForm();
        _detailsForm.ReorderRequested += DetailsForm_ReorderRequested;

        _reorderControllersMenuItem = new ToolStripMenuItem("Re-order Controllers");
        _showDetailsMenuItem = new ToolStripMenuItem("Show Current Status");
        _exitMenuItem = new ToolStripMenuItem("Exit");
        _foundConnectedTimer = new System.Windows.Forms.Timer
        {
            Interval = 1500
        };
        _hoverShowTimer = new System.Windows.Forms.Timer
        {
            Interval = 350
        };
        _hoverRotateTimer = new System.Windows.Forms.Timer
        {
            Interval = 3000
        };
        _hoverWatchTimer = new System.Windows.Forms.Timer
        {
            Interval = 150
        };

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(_reorderControllersMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_showDetailsMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_exitMenuItem);

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Text = "WinForms tray client starting",
            Visible = true
        };

        _reorderControllersMenuItem.Click += ReorderControllersMenuItem_Click;
        _showDetailsMenuItem.Click += ShowDetailsMenuItem_Click;
        _exitMenuItem.Click += ExitMenuItem_Click;
        _contextMenu.Opening += ContextMenu_Opening;
        _trayIcon.MouseDoubleClick += TrayIcon_MouseDoubleClick;
        _trayIcon.MouseMove += TrayIcon_MouseMove;
        _trayIcon.MouseClick += TrayIcon_MouseClick;
        _foundConnectedTimer.Tick += (_, _) => RestoreCurrentTrayIcon();
        _hoverShowTimer.Tick += (_, _) => ShowHoverPanelIfReady();
        _hoverRotateTimer.Tick += (_, _) => RotateHoverPanelController();
        _hoverWatchTimer.Tick += (_, _) => WatchHoverPanel();

        _pipeClient.ConnectionStateChanged += message => PostToUi(() => UpdateConnectionState(message));
        _pipeClient.StatusReceived += status => PostToUi(() => ApplyStatus(status));

        ApplyStatus(_currentStatus);
        _pipeClient.Start();
    }

    // Disposes tray resources, timers, forms, and pipe clients when the application exits.
    protected override void ExitThreadCore()
    {
        _isExiting = true;

        if (!_cleanupCompleted)
        {
            _cleanupCompleted = true;
            _foundConnectedTimer.Stop();
            _foundConnectedTimer.Dispose();
            _hoverShowTimer.Stop();
            _hoverRotateTimer.Stop();
            _hoverWatchTimer.Stop();
            _hoverShowTimer.Dispose();
            _hoverRotateTimer.Dispose();
            _hoverWatchTimer.Dispose();
            _pipeClient.Dispose();
            _contextMenu.Opening -= ContextMenu_Opening;
            _trayIcon.MouseDoubleClick -= TrayIcon_MouseDoubleClick;
            _trayIcon.MouseMove -= TrayIcon_MouseMove;
            _trayIcon.MouseClick -= TrayIcon_MouseClick;
            _reorderControllersMenuItem.Click -= ReorderControllersMenuItem_Click;
            _showDetailsMenuItem.Click -= ShowDetailsMenuItem_Click;
            _exitMenuItem.Click -= ExitMenuItem_Click;
            _trayIcon.Visible = false;

            _hoverStatusForm.Hide();
            _hoverStatusForm.Dispose();
            _detailsForm.ReorderRequested -= DetailsForm_ReorderRequested;
            _detailsForm.PrepareForExit();
            _detailsForm.Close();
            _detailsForm.Dispose();
            _trayIcon.Dispose();
        }

        base.ExitThreadCore();
    }

    // Applies one new monitor status snapshot to the tray icon, hover panel, menu, and details window.
    private void ApplyStatus(StatusPayload status)
    {
        if (_isExiting)
        {
            return;
        }

        bool wasConnected = _currentStatus.IsConnected;
        _currentStatus = status;
        _reorderControllersMenuItem.Enabled = GetReportedControllers(status).Count > 1;
        UpdateControllerMenu(status);

        if (UseCustomHoverPanel)
        {
            ClampHoverControllerIndex();
        }

        if (!status.IsConnected)
        {
            _foundConnectedTimer.Stop();
            SetTrayIcon(status);
        }
        else if (!wasConnected)
        {
            SetTrayIcon(status, showFoundConnected: true);
            _foundConnectedTimer.Stop();
            _foundConnectedTimer.Start();
        }
        else if (!_foundConnectedTimer.Enabled)
        {
            SetTrayIcon(status);
        }

        SetTrayTooltipText(UseCustomHoverPanel ? null : BuildTrayTooltip(status));

        if (UseCustomHoverPanel && _hoverStatusForm.Visible)
        {
            RefreshHoverPanelPresentation();
        }

        if (!_detailsForm.IsDisposed && _detailsForm.Visible)
        {
            _detailsForm.Apply(status, BuildDetailsText(status));
        }
    }

    // Updates the tray to a waiting state when the status pipe is not currently connected.
    private void UpdateConnectionState(string message)
    {
        if (_isExiting)
        {
            return;
        }

        if (!string.Equals(message, "Connected to monitor.", StringComparison.Ordinal))
        {
            ApplyStatus(CreateWaitingStatus(message));
        }
    }

    // Shows or restores the details form with the current status snapshot.
    private void ShowCurrentStatus()
    {
        if (_isExiting || _detailsForm.IsDisposed)
        {
            return;
        }

        _detailsForm.Apply(_currentStatus, BuildDetailsText(_currentStatus));
        if (!_detailsForm.Visible)
        {
            _detailsForm.Show();
        }

        if (_detailsForm.WindowState == FormWindowState.Minimized)
        {
            _detailsForm.WindowState = FormWindowState.Normal;
        }

        _detailsForm.BringToFront();
        _detailsForm.Activate();
    }

    // Handles tray-menu reorder clicks by forwarding them to the shared reorder request method.
    private async void ReorderControllersMenuItem_Click(object? sender, EventArgs e)
    {
        await RequestControllerReorderAsync();
    }

    // Handles details-form reorder clicks by forwarding them to the shared reorder request method.
    private async void DetailsForm_ReorderRequested(object? sender, EventArgs e)
    {
        await RequestControllerReorderAsync();
    }

    // Restores the normal tray icon after the short newly-connected highlight period.
    private void RestoreCurrentTrayIcon()
    {
        if (_isExiting)
        {
            return;
        }

        _foundConnectedTimer.Stop();
        SetTrayIcon(_currentStatus);
    }

    // Replaces the tray icon image with the icon that matches the current controller state.
    private void SetTrayIcon(StatusPayload status, bool showFoundConnected = false)
    {
        if (_isExiting)
        {
            return;
        }

        _trayIcon.Icon?.Dispose();
        _trayIcon.Icon = TrayIconFactory.Create(status, showFoundConnected);
    }

    // Marshals background pipe events back onto the WinForms UI thread.
    private void PostToUi(Action action)
    {
        if (_uiContext == null)
        {
            if (!_isExiting)
            {
                action();
            }

            return;
        }

        _uiContext.Post(_ =>
        {
            if (!_isExiting)
            {
                action();
            }
        }, null);
    }

    // Opens the current-status window from the context menu.
    private void ShowDetailsMenuItem_Click(object? sender, EventArgs e)
    {
        ShowCurrentStatus();
    }

    // Hides the hover panel before the tray context menu opens.
    private void ContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        HideHoverPanel();
    }

    // Starts the shutdown sequence from the Exit menu item.
    private void ExitMenuItem_Click(object? sender, EventArgs e)
    {
        BeginExit();
    }

    // Opens the details form when the tray icon is double-clicked.
    private void TrayIcon_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        HideHoverPanel();
        ShowCurrentStatus();
    }

    // Tracks tray hover movement so the custom hover panel can appear near the icon.
    private void TrayIcon_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!UseCustomHoverPanel || _isExiting || _contextMenu.Visible)
        {
            return;
        }

        _lastTrayHoverPoint = Cursor.Position;
        if (_hoverStatusForm.Visible)
        {
            _hoverStatusForm.UpdatePositionNear(_lastTrayHoverPoint);
            return;
        }

        _hoverShowTimer.Stop();
        _hoverShowTimer.Start();
    }

    // Hides the custom hover panel when the tray icon is clicked.
    private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (UseCustomHoverPanel)
        {
            HideHoverPanel();
        }
    }

    // Begins a controlled application shutdown without leaving a stale tray icon behind.
    private void BeginExit()
    {
        if (_isExiting)
        {
            return;
        }

        HideHoverPanel();
        _isExiting = true;
        _showDetailsMenuItem.Enabled = false;
        _exitMenuItem.Enabled = false;
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // Shows the hover panel if the cursor is still near the tray icon after the hover delay.
    private void ShowHoverPanelIfReady()
    {
        _hoverShowTimer.Stop();

        if (!UseCustomHoverPanel || _isExiting || _contextMenu.Visible)
        {
            return;
        }

        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(_currentStatus);
        if (controllers.Count == 0 || !IsCursorNearTrayAnchor(Cursor.Position))
        {
            return;
        }

        ClampHoverControllerIndex();
        RefreshHoverPanelPresentation();
        if (!_hoverStatusForm.Visible)
        {
            _hoverStatusForm.Show();
        }

        _hoverRotateTimer.Enabled = controllers.Count > 1;
        _hoverWatchTimer.Start();
    }

    // Rotates the hovered controller preview when multiple controllers are connected.
    private void RotateHoverPanelController()
    {
        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(_currentStatus);
        if (controllers.Count <= 1)
        {
            _hoverRotateTimer.Stop();
            return;
        }

        if (_hoverStatusForm.ContainsScreenPoint(Cursor.Position))
        {
            return;
        }

        _hoverControllerIndex = (_hoverControllerIndex + 1) % controllers.Count;
        RefreshHoverPanelPresentation();
    }

    // Hides the hover panel when the cursor leaves both the tray anchor and the hover form.
    private void WatchHoverPanel()
    {
        if (!_hoverStatusForm.Visible)
        {
            _hoverWatchTimer.Stop();
            return;
        }

        Point cursorPosition = Cursor.Position;
        bool cursorNearTray = IsCursorNearTrayAnchor(cursorPosition);
        bool cursorOverPanel = _hoverStatusForm.ContainsScreenPoint(cursorPosition);
        if (!cursorNearTray && !cursorOverPanel)
        {
            HideHoverPanel();
            return;
        }

        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(_currentStatus);
        _hoverRotateTimer.Enabled = controllers.Count > 1 && !cursorOverPanel;
    }

    // Rebuilds the hover panel content and syncs the tray icon to the currently previewed controller.
    private void RefreshHoverPanelPresentation()
    {
        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(_currentStatus);
        if (controllers.Count == 0)
        {
            HideHoverPanel();
            return;
        }

        ClampHoverControllerIndex();
        StatusPayload selectedController = controllers[_hoverControllerIndex];
        _hoverStatusForm.Apply(selectedController, _hoverControllerIndex + 1, controllers.Count);
        _hoverStatusForm.UpdatePositionNear(_lastTrayHoverPoint == Point.Empty ? Cursor.Position : _lastTrayHoverPoint);
        SetTrayIcon(selectedController);
    }

    // Hides the hover panel and restores the aggregate tray icon state.
    private void HideHoverPanel()
    {
        if (!UseCustomHoverPanel)
        {
            return;
        }

        _hoverShowTimer.Stop();
        _hoverRotateTimer.Stop();
        _hoverWatchTimer.Stop();
        if (_hoverStatusForm.Visible)
        {
            _hoverStatusForm.Hide();
        }

        SetTrayIcon(_currentStatus);
    }

    // Keeps the hover-panel controller index inside the current controller list bounds.
    private void ClampHoverControllerIndex()
    {
        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(_currentStatus);
        if (controllers.Count == 0)
        {
            _hoverControllerIndex = 0;
            return;
        }

        _hoverControllerIndex %= controllers.Count;
        if (_hoverControllerIndex < 0)
        {
            _hoverControllerIndex = 0;
        }
    }

    // Tests whether the cursor is still close enough to the tray icon to justify showing the hover panel.
    private bool IsCursorNearTrayAnchor(Point cursorPosition)
    {
        Point anchor = _lastTrayHoverPoint == Point.Empty ? cursorPosition : _lastTrayHoverPoint;
        var hitBox = new Rectangle(
            anchor.X - (TrayHoverHitSize.Width / 2),
            anchor.Y - (TrayHoverHitSize.Height / 2),
            TrayHoverHitSize.Width,
            TrayHoverHitSize.Height);

        return hitBox.Contains(cursorPosition);
    }

    // Expands one status payload into the multi-line raw/status dump shown in the details window.
    private static string BuildDetailsText(StatusPayload status)
    {
        var builder = new StringBuilder();
        builder.AppendLine("type: controller-status");
        builder.AppendLine($"version: {StatusMessageModels.Version}");
        builder.AppendLine($"state: {NullText(status.State)}");
        builder.AppendLine($"displayName: {NullText(status.DisplayName)}");
        builder.AppendLine($"deviceKind: {NullText(status.DeviceKind)}");
        builder.AppendLine($"isConnected: {status.IsConnected.ToString().ToLowerInvariant()}");
        builder.AppendLine($"connectionType: {NullText(status.ConnectionType)}");
        builder.AppendLine($"batteryText: {NullText(status.BatteryText)}");
        builder.AppendLine($"chargingText: {NullText(status.ChargingText)}");
        builder.AppendLine($"summaryText: {NullText(status.SummaryText)}");
        builder.AppendLine($"detailText: {NullText(status.DetailText)}");
        builder.AppendLine($"tooltipText: {NullText(status.TooltipText)}");
        builder.AppendLine($"lastUpdatedUtc: {status.LastUpdatedUtc:O}");

        if (status.Diagnostics.Length > 0)
        {
            builder.AppendLine("diagnostics:");
            foreach (string diagnostic in status.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        if (status.OtherControllers.Length > 0)
        {
            builder.AppendLine("otherControllers:");
            foreach (StatusPayload otherController in status.OtherControllers)
            {
                builder.AppendLine($"- displayName: {NullText(otherController.DisplayName)}");
                builder.AppendLine($"  deviceKind: {NullText(otherController.DeviceKind)}");
                builder.AppendLine($"  state: {NullText(otherController.State)}");
                builder.AppendLine($"  connectionType: {NullText(otherController.ConnectionType)}");
                builder.AppendLine($"  batteryText: {NullText(otherController.BatteryText)}");
                builder.AppendLine($"  chargingText: {NullText(otherController.ChargingText)}");
                builder.AppendLine($"  summaryText: {NullText(otherController.SummaryText)}");
                builder.AppendLine($"  detailText: {NullText(otherController.DetailText)}");
            }
        }

        return builder.ToString();
    }

    // Builds the compact tray tooltip string for one or more connected controllers.
    private static string BuildTrayTooltip(StatusPayload status)
    {
        if (!status.IsConnected)
        {
            return JoinTooltipParts(
                status.DisplayName ?? "Controller",
                status.SummaryText is { Length: > 0 }
                    ? status.SummaryText.Trim()
                    : "Not connected");
        }

        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(status);
        if (controllers.Count <= 1)
        {
            return JoinTooltipParts(
                status.DisplayName ?? "Controller",
                NormalizeConnectionType(status.ConnectionType),
                NormalizeChargingText(status.ChargingText),
                NormalizeBatteryText(status.BatteryText));
        }

        var parts = new List<string>
        {
            $"{controllers.Count} controllers"
        };

        for (int index = 0; index < controllers.Count; index++)
        {
            parts.Add(BuildCompactTooltipSegment(controllers[index], index + 1));
        }

        return JoinTooltipParts(parts.ToArray());
    }

    // Rebuilds the dynamic per-controller menu items above the reorder action.
    private void UpdateControllerMenu(StatusPayload status)
    {
        foreach (ToolStripMenuItem controllerMenuItem in _controllerMenuItems)
        {
            _contextMenu.Items.Remove(controllerMenuItem);
            controllerMenuItem.Click -= ControllerMenuItem_Click;
            controllerMenuItem.Dispose();
        }

        _controllerMenuItems.Clear();

        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(status);
        if (controllers.Count == 0)
        {
            return;
        }

        int insertIndex = _contextMenu.Items.IndexOf(_reorderControllersMenuItem);
        for (int index = 0; index < controllers.Count; index++)
        {
            var controllerMenuItem = new ToolStripMenuItem(BuildControllerMenuText(controllers[index], index + 1));
            controllerMenuItem.Click += ControllerMenuItem_Click;
            _controllerMenuItems.Add(controllerMenuItem);
            _contextMenu.Items.Insert(insertIndex + index, controllerMenuItem);
        }
    }

    // Opens the details form when one of the per-controller menu items is clicked.
    private void ControllerMenuItem_Click(object? sender, EventArgs e)
    {
        ShowCurrentStatus();
    }

    // Builds one compact controller segment for the multi-controller tray tooltip.
    private static string BuildCompactTooltipSegment(StatusPayload status, int index)
    {
        return JoinTooltipParts(
            $"C{index} {GetShortDisplayName(status.DisplayName)}",
            GetShortConnectionType(status.ConnectionType),
            NormalizeChargingText(status.ChargingText),
            NormalizeBatteryText(status.BatteryText));
    }

    // Builds the text used by the dynamic per-controller tray menu items.
    private static string BuildControllerMenuText(StatusPayload status, int index)
    {
        return JoinTooltipParts(
            $"Controller {index}: {NullText(status.DisplayName)}",
            NormalizeConnectionType(status.ConnectionType),
            NormalizeChargingText(status.ChargingText),
            NormalizeBatteryText(status.BatteryText));
    }

    // Collects the primary and additional connected controllers into one ordered list for the tray UI.
    private static IReadOnlyList<StatusPayload> GetReportedControllers(StatusPayload status)
    {
        if (!status.IsConnected)
        {
            return [];
        }

        StatusPayload[] otherControllers = status.OtherControllers ?? [];

        var controllers = new List<StatusPayload>(1 + otherControllers.Length)
        {
            status
        };
        controllers.AddRange(otherControllers.Where(otherController => otherController?.IsConnected == true));
        return controllers;
    }

    // Shortens verbose controller display names for the compact tooltip format.
    private static string GetShortDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "Controller";
        }

        if (displayName.Contains("DualShock 4", StringComparison.OrdinalIgnoreCase))
        {
            return "DS4";
        }

        if (displayName.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
        {
            return "DualSense";
        }

        return displayName.Trim();
    }

    // Compresses connection type names for the compact multi-controller tooltip.
    private static string GetShortConnectionType(string? connectionType)
    {
        return connectionType?.Trim().ToUpperInvariant() switch
        {
            "BLUETOOTH" => "BT",
            "USB" => "USB",
            _ => NormalizeConnectionType(connectionType)
        };
    }

    // Normalizes connection labels for tray display.
    private static string NormalizeConnectionType(string? connectionType)
    {
        return string.IsNullOrWhiteSpace(connectionType)
            ? "Connection unknown"
            : connectionType.Trim();
    }

    // Strips icon glyphs and extra annotations from the charging text for tray-friendly output.
    private static string NormalizeChargingText(string? chargingText)
    {
        if (string.IsNullOrWhiteSpace(chargingText))
        {
            return "Status unknown";
        }

        string normalized = chargingText
            .Replace("🔋", string.Empty, StringComparison.Ordinal)
            .Replace("⚡", string.Empty, StringComparison.Ordinal)
            .Replace("✅", string.Empty, StringComparison.Ordinal)
            .Replace("⚠", string.Empty, StringComparison.Ordinal)
            .Trim();

        int annotationIndex = normalized.IndexOf(" (", StringComparison.Ordinal);
        if (annotationIndex >= 0)
        {
            normalized = normalized[..annotationIndex].TrimEnd();
        }

        return normalized.ToUpperInvariant() switch
        {
            "ON BATTERY" => "On battery",
            "CHARGING" => "Charging",
            "FULLY CHARGED" => "Fully charged",
            "NOT CHARGING" => "Not charging",
            "CHARGE ERROR" => "Charge error",
            "UNKNOWN" => "Status unknown",
            _ => normalized
        };
    }

    // Normalizes battery text for tray output.
    private static string NormalizeBatteryText(string? batteryText)
    {
        return string.IsNullOrWhiteSpace(batteryText)
            ? "Battery unknown"
            : batteryText.Trim();
    }

    // Joins non-empty tooltip parts with the compact tray separator.
    private static string JoinTooltipParts(params string?[] parts)
    {
        return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    // Builds the placeholder status shown before the tray app connects to the monitor.
    private static StatusPayload CreateWaitingStatus(string summaryText)
    {
        return new StatusPayload
        {
            State = "WaitingForMonitor",
            DisplayName = "Controller Tray Client",
            DeviceKind = "Sony Controller",
            IsConnected = false,
            SummaryText = summaryText,
            DetailText = "Start BluetoothBatteryMonitor to receive live controller status.",
            TooltipText = "Controller tray client: waiting for monitor",
            Diagnostics = []
        };
    }

    // Replaces null or blank strings with a literal `null` marker in the raw details dump.
    private static string NullText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "null" : value;
    }

    // Keeps tray tooltip text inside the Windows practical length limit.
    private static string TruncateTooltip(string value)
    {
        const int maxLength = 127;
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 3)] + "...";
    }

    // Applies the final tooltip text to the NotifyIcon after optional truncation.
    private void SetTrayTooltipText(string? value)
    {
        _trayIcon.Text = string.IsNullOrWhiteSpace(value)
            ? null!
            : TruncateTooltip(value);
    }

    // Sends the reorder command to the monitor when more than one controller is currently connected.
    private async Task RequestControllerReorderAsync()
    {
        if (_isExiting || GetReportedControllers(_currentStatus).Count <= 1)
        {
            return;
        }

        await _commandClient.SendAsync(StatusMessageModels.RotateControllersCommand);
    }
}