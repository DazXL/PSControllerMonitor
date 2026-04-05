namespace PSControllerMonitor;

internal sealed class StatusDetailsForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _summaryLabel;
    private readonly Button _reorderButton;
    private readonly TabControl _tabs;
    private readonly FlowLayoutPanel _controllersPanel;
    private readonly GroupBox _diagnosticsGroup;
    private readonly TextBox _diagnosticsBox;
    private readonly TextBox _rawDetailsBox;
    private bool _allowClose;
    private StatusPayload? _lastStatus;
    private string _lastDetailsText = string.Empty;

    internal event EventHandler? ReorderRequested;

    // Builds the main details window that shows overview cards and the raw status dump.
    internal StatusDetailsForm()
    {
        Text = "Controller Tray Details";
        Icon = AppIconProvider.CreateWindowIcon();
        Width = 760;
        Height = 560;
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;

        _titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Padding = new Padding(12, 8, 12, 0)
        };

        _summaryLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Padding = new Padding(12, 4, 12, 8)
        };

        _reorderButton = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = "Re-order Controllers",
            Margin = new Padding(0)
        };
        _reorderButton.Click += ReorderButton_Click;

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(12, 0, 12, 8),
            WrapContents = false
        };
        actionPanel.Controls.Add(_reorderButton);

        _controllersPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12)
        };

        _diagnosticsBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f, FontStyle.Regular),
            BorderStyle = BorderStyle.None
        };

        _diagnosticsGroup = new GroupBox
        {
            Dock = DockStyle.Bottom,
            Height = 110,
            Text = "Diagnostics",
            Padding = new Padding(10, 6, 10, 10),
            Visible = false
        };
        _diagnosticsGroup.Controls.Add(_diagnosticsBox);

        var overviewPanel = new Panel
        {
            Dock = DockStyle.Fill
        };
        overviewPanel.Controls.Add(_controllersPanel);
        overviewPanel.Controls.Add(_diagnosticsGroup);
        overviewPanel.Controls.Add(actionPanel);
        overviewPanel.Controls.Add(_summaryLabel);
        overviewPanel.Controls.Add(_titleLabel);

        var overviewTab = new TabPage("Overview")
        {
            Padding = new Padding(0)
        };
        overviewTab.Controls.Add(overviewPanel);

        _rawDetailsBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10f, FontStyle.Regular),
            BorderStyle = BorderStyle.None
        };

        var rawPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        rawPanel.Controls.Add(_rawDetailsBox);

        var rawTab = new TabPage("Raw Details")
        {
            Padding = new Padding(0)
        };
        rawTab.Controls.Add(rawPanel);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };
        _tabs.TabPages.Add(overviewTab);
        _tabs.TabPages.Add(rawTab);

        Controls.Add(_tabs);
        FormClosing += OnFormClosing;
        Resize += (_, _) => RefreshControllerCards();
    }

    // Applies the latest status payload to all sections of the details window.
    internal void Apply(StatusPayload status, string detailsText)
    {
        _lastStatus = status;
        _lastDetailsText = detailsText;

        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(status);

        _titleLabel.Text = BuildWindowTitle(status, controllers.Count);
        _summaryLabel.Text = BuildSummaryText(status, controllers.Count);
        _reorderButton.Enabled = controllers.Count > 1;
        _rawDetailsBox.Text = detailsText;

        RebuildControllerCards(status, controllers);

        _diagnosticsBox.Text = status.Diagnostics.Length > 0
            ? string.Join(Environment.NewLine, status.Diagnostics)
            : "No diagnostics.";
        _diagnosticsGroup.Visible = status.Diagnostics.Length > 0;
    }

    // Rebuilds the controller card list from the currently reported controllers.
    private void RebuildControllerCards(StatusPayload status, IReadOnlyList<StatusPayload> controllers)
    {

        _controllersPanel.SuspendLayout();
        try
        {
            _controllersPanel.Controls.Clear();

            if (controllers.Count == 0)
            {
                _controllersPanel.Controls.Add(CreateEmptyStateLabel(status));
            }
            else
            {
                for (int index = 0; index < controllers.Count; index++)
                {
                    _controllersPanel.Controls.Add(CreateControllerCard(controllers[index], index + 1));
                }
            }
        }
        finally
        {
            _controllersPanel.ResumeLayout();
        }
    }

    // Allows the form to close for real during application shutdown instead of hiding itself.
    internal void PrepareForExit()
    {
        _allowClose = true;
    }

    // Detaches form event handlers during disposal.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reorderButton.Click -= ReorderButton_Click;
        }

        base.Dispose(disposing);
    }

    // Raises the reorder event so the tray context can ask the monitor to rotate controller order.
    private void ReorderButton_Click(object? sender, EventArgs e)
    {
        ReorderRequested?.Invoke(this, EventArgs.Empty);
    }

    // Hides the form on user close so the details window can be reopened from the tray icon.
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    // Builds one group-box card for a controller in the Overview tab.
    private Control CreateControllerCard(StatusPayload controller, int index)
    {
        int cardWidth = GetControllerCardWidth();

        var groupBox = new GroupBox
        {
            Text = $"Controller {index}",
            AutoSize = false,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Width = cardWidth,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(10, 8, 10, 10)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            MaximumSize = new Size(cardWidth - 24, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddFactRow(table, "Name", NullText(controller.DisplayName, "Controller"));
        AddFactRow(table, "Model", NullText(controller.DeviceKind, "Unknown model"));
        AddFactRow(table, "State", NullText(controller.State, "Unknown state"));
        AddFactRow(table, "Connection", controller.IsConnected ? NullText(controller.ConnectionType, "Connection unknown") : "Not connected");
        AddFactRow(table, "Battery", NullText(controller.BatteryText, "Battery unknown"));
        AddFactRow(table, "Charging", NullText(controller.ChargingText, "Status unknown"));
        AddFactRow(table, "Summary", NullText(controller.SummaryText, "No summary."));
        AddFactRow(table, "Details", NullText(controller.DetailText, "No extra detail."));
        AddFactRow(table, "Updated", FormatTimestamp(controller.LastUpdatedUtc));

        Size preferredTableSize = table.GetPreferredSize(new Size(cardWidth - 24, 0));
        groupBox.Height = preferredTableSize.Height + 32;
        groupBox.Controls.Add(table);
        return groupBox;
    }

    // Calculates the current card width so controller cards resize with the window.
    private int GetControllerCardWidth()
    {
        int availableWidth = _controllersPanel.ClientSize.Width;
        if (availableWidth <= 0)
        {
            availableWidth = ClientSize.Width - 48;
        }

        return Math.Max(560, availableWidth - 28);
    }

    // Rebuilds the controller cards after a resize using the last applied status snapshot.
    private void RefreshControllerCards()
    {
        if (_lastStatus is not StatusPayload status || !Visible)
        {
            return;
        }

        IReadOnlyList<StatusPayload> controllers = GetReportedControllers(status);
        RebuildControllerCards(status, controllers);
    }

    // Adds one label/value row to the table inside a controller overview card.
    private static void AddFactRow(TableLayoutPanel table, string labelText, string valueText)
    {
        int rowIndex = table.RowCount;
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            AutoSize = true,
            Text = labelText,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin = new Padding(0, 5, 18, 5)
        };

        var value = new Label
        {
            AutoSize = true,
            Text = valueText,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 5, 0, 5)
        };

        table.Controls.Add(label, 0, rowIndex);
        table.Controls.Add(value, 1, rowIndex);
    }

    // Creates the placeholder label shown when no controllers are currently connected.
    private static Label CreateEmptyStateLabel(StatusPayload status)
    {
        return new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
            Text = status.SummaryText ?? "No controllers are currently reporting.",
            Margin = new Padding(0)
        };
    }

    // Collects the primary and additional connected controllers into one ordered list for display.
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

    // Builds the window title shown above the controller details tabs.
    private static string BuildWindowTitle(StatusPayload status, int controllerCount)
    {
        if (!status.IsConnected)
        {
            return status.DisplayName ?? "Controller Tray Client";
        }

        return controllerCount <= 1
            ? status.DisplayName ?? "Controller Tray Client"
            : $"{controllerCount} Controllers Reporting";
    }

    // Builds the summary line shown near the top of the details window.
    private static string BuildSummaryText(StatusPayload status, int controllerCount)
    {
        if (!status.IsConnected)
        {
            return status.SummaryText ?? "No controllers are currently reporting.";
        }

        if (controllerCount <= 1)
        {
            return status.SummaryText ?? "One controller is currently reporting.";
        }

        return $"{controllerCount} controllers are currently reporting. Use Re-order Controllers to rotate which one is Controller 1.";
    }

    // Formats UTC timestamps from the monitor into local time for the details window.
    private static string FormatTimestamp(DateTime timestamp)
    {
        if (timestamp == default)
        {
            return "Unknown";
        }

        DateTime localTime = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp.ToLocalTime(),
            DateTimeKind.Local => timestamp,
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime()
        };

        return localTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // Replaces null or blank strings with a readable fallback label.
    private static string NullText(string? value, string fallback = "Unknown")
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
