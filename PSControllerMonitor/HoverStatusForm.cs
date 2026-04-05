using System.Drawing;

namespace PSControllerMonitor;

internal sealed class HoverStatusForm : Form
{
    private readonly TableLayoutPanel _layout;
    private readonly Label _titleLabel;
    private readonly Label _modelLabel;
    private readonly Label _statusLabel;
    private readonly Label _batteryLabel;
    private readonly Label _footerLabel;

    // Builds the borderless hover panel shown near the tray icon.
    internal HoverStatusForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(247, 248, 250);
        Padding = new Padding(6);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _titleLabel = new Label
        {
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        };

        _modelLabel = CreateBodyLabel(FontStyle.Bold);
        _statusLabel = CreateBodyLabel();
        _batteryLabel = CreateBodyLabel();
        _footerLabel = CreateFooterLabel();

        _layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (int index = 0; index < 5; index++)
        {
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _layout.Controls.Add(_titleLabel, 0, 0);
        _layout.Controls.Add(_modelLabel, 0, 1);
        _layout.Controls.Add(_statusLabel, 0, 2);
        _layout.Controls.Add(_batteryLabel, 0, 3);
        _layout.Controls.Add(_footerLabel, 0, 4);

        Controls.Add(_layout);
    }

    protected override bool ShowWithoutActivation => true;

    // Adds the window styles that keep the hover panel out of Alt+Tab and prevent focus stealing.
    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;

            CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= wsExToolWindow | wsExNoActivate;
            return createParams;
        }
    }

    // Fills the hover panel with the selected controller's compact status summary.
    internal void Apply(StatusPayload controller, int index, int totalCount)
    {
        _titleLabel.Text = $"Controller {index}";
        _modelLabel.Text = controller.DisplayName ?? "Controller";
        _statusLabel.Text = BuildStatusLine(controller);
        _batteryLabel.Text = controller.BatteryText ?? "Battery unknown";
        _footerLabel.Text = totalCount > 1
            ? $"{index} / {totalCount}"
            : "Hover preview";

        _layout.PerformLayout();
        PerformLayout();
    }

    // Positions the hover panel near the cursor while keeping it inside the current screen's working area.
    internal void UpdatePositionNear(Point cursorPosition)
    {
        const int offset = 16;
        Rectangle workingArea = Screen.FromPoint(cursorPosition).WorkingArea;

        int x = cursorPosition.X + offset;
        int y = cursorPosition.Y + offset;

        if (x + Width > workingArea.Right)
        {
            x = workingArea.Right - Width;
        }

        if (y + Height > workingArea.Bottom)
        {
            y = workingArea.Bottom - Height;
        }

        x = Math.Max(workingArea.Left, x);
        y = Math.Max(workingArea.Top, y);

        Location = new Point(x, y);
    }

    // Reports whether the given screen coordinate is currently inside the visible hover panel.
    internal bool ContainsScreenPoint(Point screenPoint)
    {
        return Visible && Bounds.Contains(screenPoint);
    }

    // Creates one standard body label used by the hover panel rows.
    private static Label CreateBodyLabel(FontStyle fontStyle = FontStyle.Regular)
    {
        return new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 8f, fontStyle),
            Margin = new Padding(0, 0, 0, 1),
            MaximumSize = new Size(220, 0)
        };
    }

    // Creates the smaller footer label used for hover hints and controller counts.
    private static Label CreateFooterLabel()
    {
        return new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 7f, FontStyle.Regular),
            ForeColor = Color.FromArgb(90, 98, 107),
            Margin = new Padding(0, 3, 0, 0)
        };
    }

    // Builds the compact connection and charging summary shown in the hover panel.
    private static string BuildStatusLine(StatusPayload controller)
    {
        string connectionType = controller.ConnectionType ?? "Connection unknown";
        string chargingText = controller.ChargingText ?? "Status unknown";
        return $"{connectionType} | {chargingText}";
    }
}