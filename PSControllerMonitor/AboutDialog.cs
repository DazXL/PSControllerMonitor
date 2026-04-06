namespace PSControllerMonitor;

internal static class AboutDialog
{
    internal static void Show(IWin32Window? owner = null)
    {
        using var dialog = CreateDialog();

        if (owner == null)
        {
            dialog.ShowDialog();
            return;
        }

        dialog.ShowDialog(owner);
    }

    private static Form CreateDialog()
    {
        var titleLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Text = AppMetadata.ApplicationName,
            Margin = new Padding(0, 0, 0, 8)
        };

        var versionLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Text = $"Version {AppMetadata.GetDisplayVersion()}",
            Margin = new Padding(0, 0, 0, 10)
        };

        var updatesLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Text = "Updates and more:",
            Margin = new Padding(0, 0, 0, 4)
        };

        var repoLink = new LinkLabel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Text = "GitHub repository",
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, 0, 12)
        };
        repoLink.LinkClicked += (_, _) => AppMetadata.OpenRepository();

        var okButton = new Button
        {
            Text = "OK",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.OK,
            Margin = new Padding(0)
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0)
        };
        buttonPanel.Controls.Add(okButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(titleLabel, 0, 0);
        layout.Controls.Add(versionLabel, 0, 1);
        layout.Controls.Add(updatesLabel, 0, 2);
        layout.Controls.Add(repoLink, 0, 3);
        layout.Controls.Add(buttonPanel, 0, 4);

        var dialog = new Form
        {
            Text = $"About {AppMetadata.ApplicationName}",
            Icon = AppIconProvider.CreateWindowIcon(),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(310, 165),
            AcceptButton = okButton,
            CancelButton = okButton
        };

        dialog.Controls.Add(layout);
        return dialog;
    }
}