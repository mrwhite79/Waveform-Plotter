using ScottPlot;
using ScottPlot.WinForms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace WaveformPlotter
{
    public class MainForm : Form
    {
        // ===== "C-style #defines" for defaults =====
        public const int MAX_CHANNELS = 16;

        public static readonly double[] DEFAULT_BIAS = new double[MAX_CHANNELS]
        {
            0.0, -744, 0.0, -744,
            1.0, 1.0, -744, 1.0,
            1.0, 1.0, 1.0, 1.0,
            1.0, 1.0, 1.0, 1.0
        };

        public static readonly double[] DEFAULT_SCALE = new double[MAX_CHANNELS]
        {
            0.02, 0.006105, 0.02, 0.006105,
            0.02, 0.02, 0.006105, 0.0,
            0.0, 0.0, 0.0, 0.0,
            0.0, 0.0, 0.0, 0.0
        };

        // 1 kHz data
        public const double DEFAULT_SAMPLE_INTERVAL_SEC = 1e-3;

        private const string CONFIG_FILE_NAME = "ChannelConfig.json";

        // Data model: BindingList for stable DataGridView editing
        private readonly BindingList<ChannelData> _channels = new();
        private int _numRows = 0;
        private int _numSamplesPerRow = 0;
        private double _sampleIntervalSec = DEFAULT_SAMPLE_INTERVAL_SEC;

        private AppConfig _config = new();

        // UI controls
        private FormsPlot _formsPlotTop = null!;
        private FormsPlot _formsPlotBottom = null!;
        private DataGridView _dgvChannels = null!;
        private Button _btnLoadCsv = null!;
        private Button _btnPlot = null!;
        private Button _btnSaveConfig = null!;
        private Button _btnPrev = null!;
        private Button _btnNext = null!;
        private TextBox _txtSampleInterval = null!;

        private System.Windows.Forms.Label _lblSampleInterval = null!;

        private RadioButton _rbSingleRow = null!;
        private RadioButton _rbMultiRow = null!;
        private NumericUpDown _nudRow = null!;
        private System.Windows.Forms.Label _lblRow = null!;
        private NumericUpDown _nudOverlayCount = null!;
        private System.Windows.Forms.Label _lblOverlays = null!;

        public MainForm()
        {
            Width = 1400;
            Height = 900;

            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Text = $"WaveformPlotter  v{ver}";

            // If you have a Resources-resx byte[] icon called AppIcon, this works.
            // If not, remove this block.
            try
            {
                using var ms = new MemoryStream(Properties.Resources.AppIcon);
                this.Icon = new System.Drawing.Icon(ms);
            }
            catch
            {
                // ignore icon load failures
            }

            InitializeUi();
            LoadConfigIfExists();
        }

        private void InitializeUi()
        {
            // --- Plot controls ---
            _formsPlotTop = new FormsPlot { Dock = DockStyle.Fill };
            _formsPlotBottom = new FormsPlot { Dock = DockStyle.Fill };

            // crosshairs that track the mouse on each chart
            var crosshairTop = _formsPlotTop.Plot.Add.Crosshair(0, 0);
            var crosshairBottom = _formsPlotBottom.Plot.Add.Crosshair(0, 0);

            _formsPlotTop.MouseMove += (s, e) =>
            {
                Pixel mousePixel = new Pixel(e.X, e.Y);
                Coordinates coords = _formsPlotTop.Plot.GetCoordinates(mousePixel);
                crosshairTop.Position = coords;
                _formsPlotTop.Refresh();
            };

            _formsPlotBottom.MouseMove += (s, e) =>
            {
                Pixel mousePixel = new Pixel(e.X, e.Y);
                Coordinates coords = _formsPlotBottom.Plot.GetCoordinates(mousePixel);
                crosshairBottom.Position = coords;
                _formsPlotBottom.Refresh();
            };

            // autoscale on double-click (per chart) + copy X
            _formsPlotTop.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _formsPlotTop.Plot.Axes.AutoScale();
                    CopyXAxis(_formsPlotTop, _formsPlotBottom);
                    _formsPlotTop.Refresh();
                    _formsPlotBottom.Refresh();
                }
            };

            _formsPlotBottom.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _formsPlotBottom.Plot.Axes.AutoScale();
                    CopyXAxis(_formsPlotBottom, _formsPlotTop);
                    _formsPlotTop.Refresh();
                    _formsPlotBottom.Refresh();
                }
            };

            // --- Channel grid ---
            _dgvChannels = new DataGridView
            {
                Dock = DockStyle.Left,
                Width = 460,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false
            };

            _dgvChannels.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Channel",
                DataPropertyName = "Name",
                ReadOnly = true,
                Width = 170
            });

            _dgvChannels.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = "Ch1",
                DataPropertyName = "ShowOnChart1",
                Width = 50
            });

            _dgvChannels.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = "Ch2",
                DataPropertyName = "ShowOnChart2",
                Width = 50
            });

            _dgvChannels.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Bias",
                DataPropertyName = "Bias",
                Width = 80
            });

            _dgvChannels.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Scale",
                DataPropertyName = "Scale",
                Width = 80
            });

            _dgvChannels.DataSource = _channels;

            // DGV stability: swallow data errors and commit checkbox edits immediately
            _dgvChannels.DataError += (s, e) => e.ThrowException = false;
            _dgvChannels.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_dgvChannels.IsCurrentCellDirty)
                    _dgvChannels.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _dgvChannels.CellValueChanged += DgvChannels_CellValueChanged;

            // --- Top control panel ---
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 80 };

            _btnLoadCsv = new Button { Text = "Load CSVs", Left = 10, Top = 10, Width = 100 };
            _btnLoadCsv.Click += BtnLoadCsv_Click;

            _btnPlot = new Button { Text = "Plot", Left = 120, Top = 10, Width = 80 };
            _btnPlot.Click += (s, e) => PlotFromUi();

            _btnSaveConfig = new Button { Text = "Save Config", Left = 210, Top = 10, Width = 100 };
            _btnSaveConfig.Click += (s, e) => SaveCurrentConfig();

            _lblSampleInterval = new System.Windows.Forms.Label
            {
                Text = "Sample dt (s):",
                Left = 320,
                Top = 14,
                AutoSize = true
            };

            _txtSampleInterval = new TextBox
            {
                Left = 420,
                Top = 10,
                Width = 80,
                Text = DEFAULT_SAMPLE_INTERVAL_SEC.ToString("G6", CultureInfo.InvariantCulture)
            };

            _rbSingleRow = new RadioButton { Text = "Single Row", Left = 520, Top = 10, Checked = true };
            _rbMultiRow = new RadioButton { Text = "Overlay Rows", Left = 520, Top = 35 };

            _lblRow = new System.Windows.Forms.Label { Text = "Row:", Left = 620, Top = 14, AutoSize = true };

            _nudRow = new NumericUpDown { Left = 660, Top = 10, Width = 80, Minimum = 0, Maximum = 0 };

            _lblOverlays = new System.Windows.Forms.Label { Text = "# overlays:", Left = 750, Top = 14, AutoSize = true };

            _nudOverlayCount = new NumericUpDown
            {
                Left = 830,
                Top = 10,
                Width = 60,
                Minimum = 1,
                Maximum = 20,
                Value = 5
            };

            _btnPrev = new Button { Text = "<", Left = 900, Top = 8, Width = 40 };
            _btnPrev.Click += (s, e) => StepRows(-1);

            _btnNext = new Button { Text = ">", Left = 945, Top = 8, Width = 40 };
            _btnNext.Click += (s, e) => StepRows(+1);

            topPanel.Controls.AddRange(new Control[]
            {
                _btnLoadCsv, _btnPlot, _btnSaveConfig,
                _lblSampleInterval, _txtSampleInterval,
                _rbSingleRow, _rbMultiRow,
                _lblRow, _nudRow,
                _lblOverlays, _nudOverlayCount,
                _btnPrev, _btnNext
            });

            // --- Charts stacked vertically ---
            var chartsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            chartsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chartsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chartsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            chartsLayout.Controls.Add(_formsPlotTop, 0, 0);
            chartsLayout.Controls.Add(_formsPlotBottom, 0, 1);

            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(chartsLayout);
            rightPanel.Controls.Add(topPanel);

            Controls.Add(rightPanel);
            Controls.Add(_dgvChannels);
        }

        // =========================
        // DGV event handler
        // =========================
        private void DgvChannels_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _channels.Count)
                return;

            var row = _dgvChannels.Rows[e.RowIndex];
            var channel = _channels[e.RowIndex];

            // Column indexes: 0=Name, 1=Ch1, 2=Ch2, 3=Bias, 4=Scale
            if (e.ColumnIndex == 1)
            {
                object? val = row.Cells[e.ColumnIndex].Value;
                channel.ShowOnChart1 = val is bool b && b;
            }
            else if (e.ColumnIndex == 2)
            {
                object? val = row.Cells[e.ColumnIndex].Value;
                channel.ShowOnChart2 = val is bool b && b;
            }
            else if (e.ColumnIndex == 3)
            {
                string? s = Convert.ToString(row.Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture);
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double bias))
                    channel.Bias = bias;
            }
            else if (e.ColumnIndex == 4)
            {
                string? s = Convert.ToString(row.Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture);
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
                    channel.Scale = scale;
            }
        }

        // =========================
        // Row navigation
        // =========================
        private void StepRows(int direction)
        {
            if (_numRows == 0)
                return;

            int step = _rbSingleRow.Checked ? 1 : (int)_nudOverlayCount.Value;
            if (step < 1) step = 1;

            int cur = (int)_nudRow.Value;
            int newRow = cur + direction * step;

            if (newRow < 0) newRow = 0;
            if (newRow >= _numRows) newRow = _numRows - 1;

            _nudRow.Value = newRow;
            PlotFromUi();
        }

        // =========================
        // Plotting based on UI state
        // =========================
        private void PlotFromUi()
        {
            if (_channels.Count == 0 || _numRows == 0 || _numSamplesPerRow == 0)
            {
                MessageBox.Show("Load CSV files first.", "No data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!double.TryParse(_txtSampleInterval.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out _sampleIntervalSec))
            {
                MessageBox.Show("Invalid sample interval. Use a numeric value in seconds.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _dgvChannels.EndEdit();

            _formsPlotTop.Plot.Clear();
            _formsPlotBottom.Plot.Clear();

            int baseRow = (int)_nudRow.Value;

            if (_rbSingleRow.Checked)
                PlotSingleRow(baseRow);
            else
                PlotRowOverlay(baseRow, (int)_nudOverlayCount.Value);

            _formsPlotTop.Plot.Axes.AutoScale();
            CopyXAxis(_formsPlotTop, _formsPlotBottom);

            _formsPlotTop.Refresh();
            _formsPlotBottom.Refresh();
        }

        // =========================
        // Event handlers
        // =========================
        private void BtnLoadCsv_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Multiselect = true,
                Title = "Select CSV files (one per channel)"
            };

            if (ofd.ShowDialog(this) != DialogResult.OK)
                return;

            if (ofd.FileNames.Length == 0)
                return;

            _channels.Clear();
            _numRows = 0;
            _numSamplesPerRow = 0;

            // Build a fast lookup of config keys for partial-match scoring
            var cfgKeys = _config.FileMap.Keys.ToList();

            int channelIndex = 0;
            foreach (string filePath in ofd.FileNames)
            {
                if (channelIndex >= MAX_CHANNELS)
                {
                    MessageBox.Show($"Exceeded MAX_CHANNELS ({MAX_CHANNELS}). Extra files will be ignored.",
                        "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                }

                var ch = LoadChannelFromCsv(filePath, channelIndex, cfgKeys);
                _channels.Add(ch);
                channelIndex++;
            }

            if (_channels.Count == 0)
                return;

            _numRows = _channels[0].Data.GetLength(0);
            _numSamplesPerRow = _channels[0].Data.GetLength(1);

            bool shapeMismatch = _channels.Any(c =>
                c.Data.GetLength(0) != _numRows ||
                c.Data.GetLength(1) != _numSamplesPerRow);

            if (shapeMismatch)
            {
                MessageBox.Show("Not all CSVs have the same number of rows/samples. Viewer assumes aligned datasets.",
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (_numRows > 0)
                _nudRow.Maximum = _numRows - 1;
        }

        // =========================
        // Plotting helpers
        // =========================
        private void PlotSingleRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _numRows)
                return;

            double dt = _sampleIntervalSec;
            int N = _numSamplesPerRow;

            double[] t = new double[N];
            for (int i = 0; i < N; i++)
                t[i] = i * dt;

            var palette = new ScottPlot.Palettes.Category10();

            for (int chIndex = 0; chIndex < _channels.Count; chIndex++)
            {
                var ch = _channels[chIndex];
                ScottPlot.Color color = palette.GetColor(chIndex);

                double[] y = new double[N];
                for (int i = 0; i < N; i++)
                {
                    double raw = ch.Data[rowIndex, i];
                    y[i] = (raw + ch.Bias) * ch.Scale;
                }

                if (ch.ShowOnChart1)
                {
                    var sp1 = _formsPlotTop.Plot.Add.Scatter(t, y);
                    sp1.Color = color;
                    sp1.LegendText = ch.Name;
                }

                if (ch.ShowOnChart2)
                {
                    var sp2 = _formsPlotBottom.Plot.Add.Scatter(t, y);
                    sp2.Color = color;
                    sp2.LegendText = ch.Name;
                }
            }

            _formsPlotTop.Plot.Title($"Row {rowIndex} (Chart 1)");
            _formsPlotBottom.Plot.Title($"Row {rowIndex} (Chart 2)");
            _formsPlotTop.Plot.XLabel("Time (s)");
            _formsPlotBottom.Plot.XLabel("Time (s)");
            _formsPlotTop.Plot.ShowLegend();
            _formsPlotBottom.Plot.ShowLegend();
        }

        private void PlotRowOverlay(int baseRow, int overlayCount)
        {
            if (baseRow < 0) baseRow = 0;
            if (baseRow >= _numRows) baseRow = _numRows - 1;
            if (overlayCount < 1) overlayCount = 1;
            if (overlayCount > 20) overlayCount = 20;

            double dt = _sampleIntervalSec;
            int N = _numSamplesPerRow;

            double[] t = new double[N];
            for (int i = 0; i < N; i++)
                t[i] = i * dt;

            var palette = new ScottPlot.Palettes.Category10();

            for (int offset = 0; offset < overlayCount; offset++)
            {
                int r = baseRow + offset;
                if (r >= _numRows)
                    break;

                double frac = (overlayCount == 1) ? 0.0 : (double)offset / (overlayCount - 1);

                for (int chIndex = 0; chIndex < _channels.Count; chIndex++)
                {
                    var ch = _channels[chIndex];
                    ScottPlot.Color baseColor = palette.GetColor(chIndex);
                    ScottPlot.Color overlayColor = baseColor.Darken(0.5 * frac);

                    double[] y = new double[N];
                    for (int i = 0; i < N; i++)
                    {
                        double raw = ch.Data[r, i];
                        y[i] = (raw + ch.Bias) * ch.Scale;
                    }

                    if (ch.ShowOnChart1)
                    {
                        var sp1 = _formsPlotTop.Plot.Add.Scatter(t, y);
                        sp1.Color = overlayColor;
                        if (offset == 0)
                            sp1.LegendText = ch.Name;
                    }

                    if (ch.ShowOnChart2)
                    {
                        var sp2 = _formsPlotBottom.Plot.Add.Scatter(t, y);
                        sp2.Color = overlayColor;
                        if (offset == 0)
                            sp2.LegendText = ch.Name;
                    }
                }
            }

            _formsPlotTop.Plot.Title($"Rows {baseRow} overlay ({overlayCount} max) (Chart 1)");
            _formsPlotBottom.Plot.Title($"Rows {baseRow} overlay ({overlayCount} max) (Chart 2)");
            _formsPlotTop.Plot.XLabel("Time (s)");
            _formsPlotBottom.Plot.XLabel("Time (s)");
            _formsPlotTop.Plot.ShowLegend();
            _formsPlotBottom.Plot.ShowLegend();
        }

        // =========================
        // CSV loading + config match
        // =========================
        private ChannelData LoadChannelFromCsv(string filePath, int channelIndex, List<string> cfgKeys)
        {
            var lines = File.ReadAllLines(filePath);

            var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (nonEmpty.Length <= 1)
                throw new InvalidOperationException("CSV has no data rows (after header): " + filePath);

            // First non-empty line is header -> ignore
            var dataLines = nonEmpty.Skip(1).ToArray();
            int numRows = dataLines.Length;

            // Ignore first 2 columns, use min sample count across rows
            int minSamples = int.MaxValue;
            foreach (string line in dataLines)
            {
                string[] parts = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None);
                int sampleCols = Math.Max(0, parts.Length - 2);
                if (sampleCols < minSamples)
                    minSamples = sampleCols;
            }

            if (minSamples <= 0)
                throw new InvalidOperationException("No data columns found (after skipping timestamp columns): " + filePath);

            int numSamples = minSamples;
            double[,] data = new double[numRows, numSamples];

            for (int r = 0; r < numRows; r++)
            {
                string[] parts = dataLines[r].Split(new[] { ',', ';', '\t' }, StringSplitOptions.None);

                for (int c = 0; c < numSamples; c++)
                {
                    int idx = 2 + c;
                    double val = 0.0;
                    if (idx < parts.Length)
                    {
                        string token = parts[idx].Trim();
                        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                            val = 0.0;
                    }
                    data[r, c] = val;
                }
            }

            string rawName = Path.GetFileNameWithoutExtension(filePath);
            string normName = NormalizeKey(rawName);

            // Find best config match (exact or partial) using normalized keys
            string? bestKey = FindBestConfigKey(normName, cfgKeys);

            // Defaults
            double bias = 0.0;
            double scale = 1.0;
            bool show1 = true;
            bool show2 = false;

            if (bestKey != null && _config.FileMap.TryGetValue(bestKey, out var map))
            {
                bias = map.Bias;
                scale = map.Scale;
                show1 = map.ShowOnChart1;
                show2 = map.ShowOnChart2;
            }
            else
            {
                if (channelIndex < MAX_CHANNELS)
                {
                    bias = DEFAULT_BIAS[channelIndex];
                    scale = DEFAULT_SCALE[channelIndex];
                }
            }

            return new ChannelData
            {
                Name = rawName,
                Key = normName,
                Data = data,
                Bias = bias,
                Scale = scale,
                ShowOnChart1 = show1,
                ShowOnChart2 = show2
            };
        }

        // =========================
        // Config handling (v2 map) + backward compat (v1 list)
        // =========================
        private void LoadConfigIfExists()
        {
            _config = new AppConfig
            {
                SampleIntervalSec = DEFAULT_SAMPLE_INTERVAL_SEC
            };
            _txtSampleInterval.Text = DEFAULT_SAMPLE_INTERVAL_SEC.ToString("G6", CultureInfo.InvariantCulture);

            if (!File.Exists(CONFIG_FILE_NAME))
                return;

            try
            {
                string json = File.ReadAllText(CONFIG_FILE_NAME);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (cfg != null)
                {
                    _config = cfg;

                    // Backward-compat: if old Channels[] exists, ingest into FileMap (normalized)
                    if (_config.Channels != null && _config.Channels.Count > 0)
                    {
                        foreach (var old in _config.Channels)
                        {
                            var k = NormalizeKey(old.Name);
                            _config.FileMap[k] = new ChannelConfigEntryV2
                            {
                                Bias = old.Bias,
                                Scale = old.Scale,
                                ShowOnChart1 = old.ShowOnChart1,
                                ShowOnChart2 = old.ShowOnChart2
                            };
                        }
                    }

                    if (_config.SampleIntervalSec > 0)
                        _sampleIntervalSec = _config.SampleIntervalSec;

                    _txtSampleInterval.Text = _sampleIntervalSec.ToString("G6", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load config: {ex.Message}",
                    "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                _config = new AppConfig
                {
                    SampleIntervalSec = DEFAULT_SAMPLE_INTERVAL_SEC
                };
                _txtSampleInterval.Text = DEFAULT_SAMPLE_INTERVAL_SEC.ToString("G6", CultureInfo.InvariantCulture);
            }
        }

        private void SaveCurrentConfig()
        {
            _dgvChannels.EndEdit();

            if (!double.TryParse(_txtSampleInterval.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double dt))
            {
                MessageBox.Show("Invalid sample interval, not saved.",
                    "Config", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                dt = DEFAULT_SAMPLE_INTERVAL_SEC;
            }

            _config.SampleIntervalSec = dt;

            // Save mapping by normalized key (filename-derived)
            _config.FileMap.Clear();
            foreach (var ch in _channels)
            {
                string k = NormalizeKey(ch.Name);
                _config.FileMap[k] = new ChannelConfigEntryV2
                {
                    Bias = ch.Bias,
                    Scale = ch.Scale,
                    ShowOnChart1 = ch.ShowOnChart1,
                    ShowOnChart2 = ch.ShowOnChart2
                };
            }

            // keep old list empty (we're now using FileMap)
            _config.Channels = new List<ChannelConfigEntryV1>();

            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(CONFIG_FILE_NAME, json);
            MessageBox.Show("Config saved.", "Config",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // =========================
        // X-axis copy helper
        // =========================
        private void CopyXAxis(FormsPlot src, FormsPlot dst)
        {
            var srcBottom = src.Plot.Axes.Bottom;
            var dstBottom = dst.Plot.Axes.Bottom;

            if (Math.Abs(srcBottom.Min - dstBottom.Min) < 1e-9 &&
                Math.Abs(srcBottom.Max - dstBottom.Max) < 1e-9)
                return;

            dstBottom.Min = srcBottom.Min;
            dstBottom.Max = srcBottom.Max;
            dst.Refresh();
        }

        // =========================
        // Name normalization + partial match
        // =========================

        // Normalization: UPPERCASE, remove extension, replace non [A-Z0-9] with '_', collapse '_' runs, trim '_'
        private static string NormalizeKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string s = name.Trim();

            // If user accidentally passed full filename, remove extension
            s = Path.GetFileNameWithoutExtension(s);

            s = s.ToUpperInvariant();

            // replace non-alnum with underscore
            char[] chars = s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
            s = new string(chars);

            // collapse multiple underscores
            while (s.Contains("__"))
                s = s.Replace("__", "_");

            s = s.Trim('_');

            return s;
        }

        // Find best matching config key for the current normalized filename
        // Priority: exact > contains > token overlap score
        private static string? FindBestConfigKey(string normName, List<string> cfgKeys)
        {
            if (string.IsNullOrWhiteSpace(normName) || cfgKeys == null || cfgKeys.Count == 0)
                return null;

            // Normalize config keys once (they should already be normalized, but be safe)
            // We'll score using normalized forms
            string? exact = cfgKeys.FirstOrDefault(k => string.Equals(k, normName, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            // Contains match (either direction)
            var contains = cfgKeys
                .Select(k => new { Key = k, Score = ContainsScore(normName, k) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (contains != null)
                return contains.Key;

            // Token overlap score
            var tokensA = Tokenize(normName);
            if (tokensA.Count == 0)
                return null;

            string? bestKey = null;
            int bestScore = 0;

            foreach (var k in cfgKeys)
            {
                var tokensB = Tokenize(k);
                int score = tokensA.Intersect(tokensB).Count();
                if (score > bestScore)
                {
                    bestScore = score;
                    bestKey = k;
                }
            }

            return bestScore > 0 ? bestKey : null;
        }

        private static int ContainsScore(string a, string b)
        {
            // Higher score = longer match
            // If either contains the other, return the length of the shorter string as score
            if (a.Contains(b, StringComparison.OrdinalIgnoreCase))
                return b.Length;
            if (b.Contains(a, StringComparison.OrdinalIgnoreCase))
                return a.Length;
            return 0;
        }

        private static HashSet<string> Tokenize(string s)
        {
            var tokens = s.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                          .Where(t => t.Length >= 2)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return tokens;
        }
    }

    // =========================
    // Helper classes
    // =========================
    public class ChannelData
    {
        public string Name { get; set; } = string.Empty; // raw filename w/o extension
        public string Key { get; set; } = string.Empty;  // normalized key for config matching
        public double[,] Data { get; set; } = new double[0, 0];
        public double Bias { get; set; }
        public double Scale { get; set; }
        public bool ShowOnChart1 { get; set; } = true;
        public bool ShowOnChart2 { get; set; } = false;
    }

    // Old config shape (v1) - kept only for backward compatibility loading
    public class ChannelConfigEntryV1
    {
        public string Name { get; set; } = string.Empty;
        public double Bias { get; set; }
        public double Scale { get; set; }
        public bool ShowOnChart1 { get; set; } = true;
        public bool ShowOnChart2 { get; set; } = false;
    }

    // New config shape (v2) - mapping by normalized filename key
    public class ChannelConfigEntryV2
    {
        public double Bias { get; set; }
        public double Scale { get; set; }
        public bool ShowOnChart1 { get; set; } = true;
        public bool ShowOnChart2 { get; set; } = false;
    }

    public class AppConfig
    {
        public double SampleIntervalSec { get; set; } = MainForm.DEFAULT_SAMPLE_INTERVAL_SEC;

        // v2 preferred: mapping by normalized filename key
        public Dictionary<string, ChannelConfigEntryV2> FileMap { get; set; }
            = new Dictionary<string, ChannelConfigEntryV2>(StringComparer.OrdinalIgnoreCase);

        // v1 legacy: list by name (we ingest this into FileMap on load, and write empty list on save)
        public List<ChannelConfigEntryV1> Channels { get; set; } = new();
    }
}