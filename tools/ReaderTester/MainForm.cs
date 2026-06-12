namespace ReaderTester;

public sealed class MainForm : Form
{
    private readonly ReaderClient _reader = new();

    // Connection row
    private readonly TextBox _ipBox = new() { Text = "192.168.1.190", Width = 120 };
    private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 27011, Width = 70 };
    private readonly Button _connectBtn = new() { Text = "Connect", Width = 90 };
    private readonly Button _disconnectBtn = new() { Text = "Disconnect", Width = 90, Enabled = false };
    private readonly Label _statusLabel = new() { Text = "● Disconnected", ForeColor = Color.Firebrick, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };

    // Command row
    private readonly ComboBox _modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Button _setModeBtn = new() { Text = "Set Mode", Width = 80, Enabled = false };
    private readonly Button _getModeBtn = new() { Text = "Get Mode", Width = 80, Enabled = false };
    private readonly NumericUpDown _powerBox = new() { Minimum = 0, Maximum = 30, Value = 20, Width = 55 };
    private readonly Button _setPowerBtn = new() { Text = "Set Power", Width = 85, Enabled = false };
    private readonly Button _beepBtn = new() { Text = "Beep", Width = 70, Enabled = false };

    // Tag list
    private readonly ListView _tagList = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10)
    };
    private readonly Label _countsLabel = new() { Text = "Unique: 0   Total reads: 0", AutoSize = true };
    private readonly Button _clearBtn = new() { Text = "Clear", Width = 70 };

    // Log
    private readonly CheckBox _rawHexCheck = new() { Text = "Show raw RX hex", AutoSize = true };
    private readonly TextBox _logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BackColor = Color.FromArgb(18, 18, 18), ForeColor = Color.Gainsboro
    };

    private readonly Dictionary<string, (ListViewItem Item, int Count)> _tags = new();
    private int _totalReads;

    public MainForm()
    {
        Text = "UHF Reader Tester";
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(700, 500);

        _modeBox.Items.AddRange(["Answer", "Real-Time"]);
        _modeBox.SelectedIndex = 1;

        _tagList.Columns.Add("EPC", 320);
        _tagList.Columns.Add("Ant", 50);
        _tagList.Columns.Add("RSSI", 60);
        _tagList.Columns.Add("Count", 60);
        _tagList.Columns.Add("First Seen", 110);
        _tagList.Columns.Add("Last Seen", 110);

        Controls.Add(BuildLayout());
        WireEvents();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // connection
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // commands
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));         // tags
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // counts row
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));         // log

        var connRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        connRow.Controls.AddRange([
            MakeLabel("Reader IP:"), _ipBox, MakeLabel("Port:"), _portBox,
            _connectBtn, _disconnectBtn, Spacer(15), _statusLabel
        ]);

        var cmdRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        cmdRow.Controls.AddRange([
            MakeLabel("Mode:"), _modeBox, _setModeBtn, _getModeBtn, Spacer(15),
            MakeLabel("Power (dBm):"), _powerBox, _setPowerBtn, Spacer(15), _beepBtn
        ]);

        var countsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        countsRow.Controls.AddRange([_countsLabel, Spacer(20), _clearBtn, Spacer(20), _rawHexCheck]);

        root.Controls.Add(connRow, 0, 0);
        root.Controls.Add(cmdRow, 0, 1);
        root.Controls.Add(_tagList, 0, 2);
        root.Controls.Add(countsRow, 0, 3);
        root.Controls.Add(_logBox, 0, 4);
        return root;
    }

    private static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 0) };

    private static Panel Spacer(int w) => new() { Width = w, Height = 1 };

    private void WireEvents()
    {
        _reader.Log += msg => SafeInvoke(() => AppendLog(msg));
        _reader.RawHex += hex => SafeInvoke(() => { if (_rawHexCheck.Checked) AppendLog($"RAW: {hex}"); });
        _reader.TagRead += tag => SafeInvoke(() => OnTagRead(tag));
        _reader.Disconnected += reason => SafeInvoke(() => SetConnectedUi(false, reason));

        _connectBtn.Click += async (_, _) => await ConnectAsync();
        _disconnectBtn.Click += (_, _) => _reader.Disconnect();
        _clearBtn.Click += (_, _) => ClearTags();

        _setModeBtn.Click += async (_, _) =>
        {
            byte mode = (byte)(_modeBox.SelectedIndex == 0 ? 0x00 : 0x01);
            bool ok = await _reader.SetModeAsync(mode);
            AppendLog(ok
                ? $"Mode set to {_modeBox.Text}." + (mode == 0 ? " (Answer mode: reader stops streaming — tag list will go quiet.)" : " Tags will stream as they are read.")
                : "Failed to set mode.");
        };

        _getModeBtn.Click += async (_, _) => AppendLog($"Current mode: {await _reader.GetModeAsync()}");

        _setPowerBtn.Click += async (_, _) =>
        {
            bool ok = await _reader.SetPowerAsync((byte)_powerBox.Value);
            AppendLog(ok ? $"Power set to {_powerBox.Value} dBm." : "Failed to set power.");
        };

        _beepBtn.Click += async (_, _) =>
        {
            bool ok = await _reader.BeepAsync();
            AppendLog(ok ? "Beep OK." : "Beep failed.");
        };

        FormClosed += (_, _) => _reader.Dispose();
    }

    private async Task ConnectAsync()
    {
        _connectBtn.Enabled = false;
        try
        {
            await _reader.ConnectAsync(_ipBox.Text.Trim(), (int)_portBox.Value);
            SetConnectedUi(true, null);

            // Identify the reader and report its current state — but change nothing.
            var info = await _reader.GetReaderInfoAsync();
            AppendLog(info is not null ? $"Reader: {info}" : "Reader did not answer info command (it may still stream tags).");
            AppendLog($"Current mode: {await _reader.GetModeAsync()}");
        }
        catch (Exception ex)
        {
            AppendLog($"Connect failed: {ex.Message}");
            _connectBtn.Enabled = true;
        }
    }

    private void SetConnectedUi(bool connected, string? reason)
    {
        _connectBtn.Enabled = !connected;
        _disconnectBtn.Enabled = connected;
        _setModeBtn.Enabled = connected;
        _getModeBtn.Enabled = connected;
        _setPowerBtn.Enabled = connected;
        _beepBtn.Enabled = connected;
        _statusLabel.Text = connected ? "● Connected" : "● Disconnected";
        _statusLabel.ForeColor = connected ? Color.SeaGreen : Color.Firebrick;
        if (!connected && reason is not null)
            AppendLog($"Disconnected ({reason}).");
    }

    private void OnTagRead(TagReading tag)
    {
        _totalReads++;
        string time = tag.Time.ToString("HH:mm:ss.fff");
        string rssi = tag.Rssi?.ToString() ?? "-";

        if (_tags.TryGetValue(tag.Epc, out var existing))
        {
            var (item, count) = existing;
            count++;
            item.SubItems[1].Text = tag.Antenna.ToString();
            item.SubItems[2].Text = rssi;
            item.SubItems[3].Text = count.ToString();
            item.SubItems[5].Text = time;
            _tags[tag.Epc] = (item, count);
        }
        else
        {
            var item = new ListViewItem([tag.Epc, tag.Antenna.ToString(), rssi, "1", time, time]);
            _tagList.Items.Insert(0, item);
            _tags[tag.Epc] = (item, 1);
        }

        _countsLabel.Text = $"Unique: {_tags.Count}   Total reads: {_totalReads}";
    }

    private void ClearTags()
    {
        _tags.Clear();
        _tagList.Items.Clear();
        _totalReads = 0;
        _countsLabel.Text = "Unique: 0   Total reads: 0";
    }

    private void AppendLog(string msg)
    {
        if (_logBox.TextLength > 200_000) _logBox.Clear();
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
