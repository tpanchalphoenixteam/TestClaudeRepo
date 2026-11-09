namespace FlipkartPriceUpdater;

/// <summary>Grid to enter SKU / Selling Price / MRP, and an OK button that applies the changes.</summary>
public sealed class MainForm : Form
{
    const int ColSku = 0, ColPrice = 1, ColMrp = 2, ColStatus = 3;

    readonly DataGridView _grid = new();
    readonly TextBox _log = new();
    readonly Button _ok = new() { Text = "OK - Update Prices", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    readonly Button _clear = new() { Text = "Clear", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    readonly CheckBox _useChrome = new() { Text = "Use my installed Google Chrome", Checked = true, AutoSize = true };
    bool _running;

    public MainForm()
    {
        Text = "Flipkart Seller Hub - Update SKU Prices";
        Size = new Size(820, 640);
        MinimumSize = new Size(600, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);

        var help = new Label
        {
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
            Text = "Enter one SKU per row with its new selling price (MRP is optional).\n" +
                   "Tip: copy SKU / Price columns from Excel and press Ctrl+V in the grid.",
        };

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersWidth = 50;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SKU ID", FillWeight = 35 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Selling Price", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "MRP (optional)", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 39, ReadOnly = true });
        _grid.Columns[ColStatus].DefaultCellStyle.ForeColor = Color.DimGray;
        _grid.KeyDown += OnGridKeyDown;
        _grid.RowPostPaint += (_, e) =>
            TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), _grid.Font,
                new Rectangle(e.RowBounds.Left, e.RowBounds.Top, _grid.RowHeadersWidth - 6, e.RowBounds.Height),
                Color.Gray, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9f);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { _ok, _clear, _useChrome });
        _useChrome.Margin = new Padding(3, 8, 20, 3);
        _ok.Click += OnOkClick;
        _clear.Click += (_, _) => { if (!_running) _grid.Rows.Clear(); };
        AcceptButton = null; // Enter moves between grid cells instead of submitting

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(help, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(_log, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        FormClosing += (_, e) =>
        {
            if (_running && MessageBox.Show(this, "Prices are still being updated. Close anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                e.Cancel = true;
        };
    }

    // ------------------------------------------------------------------ paste from Excel
    void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_running) return;
        if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsText())
        {
            var rows = PriceParser.SplitText(Clipboard.GetText());
            var r = _grid.CurrentCell?.RowIndex ?? 0;
            var c0 = Math.Min(_grid.CurrentCell?.ColumnIndex ?? 0, ColMrp);
            foreach (var parts in rows)
            {
                if (r >= _grid.Rows.Count - 1) _grid.Rows.Add();
                for (var i = 0; i < parts.Length && c0 + i <= ColMrp; i++)
                    _grid.Rows[r].Cells[c0 + i].Value = parts[i];
                _grid.Rows[r].Cells[ColStatus].Value = null;
                r++;
            }
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete && !_grid.IsCurrentCellInEditMode)
        {
            foreach (DataGridViewCell cell in _grid.SelectedCells)
                if (cell.ColumnIndex != ColStatus) cell.Value = null;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ OK
    async void OnOkClick(object? sender, EventArgs e)
    {
        if (_running) return;
        _grid.EndEdit();

        var rows = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => (r.Index + 1, Cell(r, ColSku), Cell(r, ColPrice), Cell(r, ColMrp)));
        var (changes, errors) = PriceParser.Validate(rows);

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "Please fix these rows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (changes.Count == 0)
        {
            MessageBox.Show(this, "Enter at least one SKU and price.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetRunning(true);
        foreach (var c in changes) SetStatus(c, "waiting...", Color.DimGray);
        try
        {
            await using var hub = new SellerHub(AskUserAsync, Log);
            Log("Starting browser...");
            await hub.StartAsync(_useChrome.Checked);
            await hub.EnsureLoggedInAsync();
            await hub.OpenListingsPageAsync();

            for (var i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                SetStatus(c, "updating...", Color.RoyalBlue);
                Log($"[{i + 1}/{changes.Count}] {c.Sku} -> {c.Price}{(c.Mrp is null ? "" : $" (MRP {c.Mrp})")}");
                await hub.UpdateAsync(c);
                SetStatus(c, c.Status, c.Succeeded ? Color.ForestGreen
                    : c.Status.StartsWith("CHECK") ? Color.DarkOrange : Color.Firebrick);
                Log("    " + c.Status);
            }

            var ok = changes.Count(c => c.Succeeded);
            MessageBox.Show(this,
                $"Updated {ok} of {changes.Count} SKU(s).\n\n" +
                string.Join("\n", changes.Select(c => $"{c.Sku}: {c.Status}")) +
                "\n\nCheck the browser if needed. Click OK to close the browser.",
                "Done", MessageBoxButtons.OK, ok == changes.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Something went wrong", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetRunning(false);
        }
    }

    Task AskUserAsync(string message)
    {
        MessageBox.Show(this, message, "Action needed in the browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return Task.CompletedTask;
    }

    static string? Cell(DataGridViewRow r, int col) => r.Cells[col].Value?.ToString();

    void SetStatus(PriceChange c, string text, Color color)
    {
        var cell = _grid.Rows[c.Row - 1].Cells[ColStatus];
        cell.Value = text;
        cell.Style.ForeColor = color;
    }

    void SetRunning(bool running)
    {
        _running = running;
        _ok.Enabled = _clear.Enabled = _useChrome.Enabled = !running;
        _grid.ReadOnly = running;
        _grid.AllowUserToAddRows = !running;
        UseWaitCursor = running;
        _ok.Text = running ? "Updating..." : "OK - Update Prices";
    }

    void Log(string message) => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
}
