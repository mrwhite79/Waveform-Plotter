
using ScottPlot;
using ScottPlot.WinForms;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.IO;

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
            Text = "Multi-Channel CSV A2D Viewer";
            Width = 1400;
            Height = 900;

            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            this.Text = $"WaveformPlotter  v{ver}";

            using (var ms = new MemoryStream(Properties.Resources.AppIcon))
            {
                this.Icon = new Icon(ms);
            }

            InitializeUi();
            LoadConfigIfExists();
        }

        private void SyncX(FormsPlot src, FormsPlot dst)
        {
            double xmin = src.Plot.Axes.Bottom.Min;
            double xmax = src.Plot.Axes.Bottom.Max;

            var destAxis = dst.Plot.Axes.Bottom;

            if (Math.Abs(destAxis.Min - xmin) > 1e-9 ||
                Math.Abs(destAxis.Max - xmax) > 1e-9)
            {
                destAxis.Min = xmin;
                destAxis.Max = xmax;
                dst.Refresh();
            }
        }
        private void InitializeUi()
        {
            // --- Plot controls ---
            _formsPlotTop = new FormsPlot
            {
                Dock = DockStyle.Fill
            };

            _formsPlotBottom = new FormsPlot
            {
                Dock = DockStyle.Fill
            };

            // default ScottPlot interactions already give pan/zoom with mouse wheel and click-drag
            // add crosshairs that track the mouse on each chart
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

            // autoscale on double-click (per chart)
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
            _dgvChannels.DataError += (s, e) =>
            {
                e.ThrowException = false;
            };

            _dgvChannels.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_dgvChannels.IsCurrentCellDirty)
                    _dgvChannels.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            _dgvChannels.CellValueChanged += DgvChannels_CellValueChanged;

            // --- Top control panel ---
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80
            };

            _btnLoadCsv = new Button
            {
                Text = "Load CSVs",
                Left = 10,
                Top = 10,
                Width = 100
            };
            _btnLoadCsv.Click += BtnLoadCsv_Click;

            _btnPlot = new Button
            {
                Text = "Plot",
                Left = 120,
                Top = 10,
                Width = 80
            };
            _btnPlot.Click += (s, e) => PlotFromUi();

            _btnSaveConfig = new Button
            {
                Text = "Save Config",
                Left = 210,
                Top = 10,
                Width = 100
            };
            _btnSaveConfig.Click += BtnSaveConfig_Click;

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

            // Row and overlay controls
            _rbSingleRow = new RadioButton
            {
                Text = "Single Row",
                Left = 520,
                Top = 10,
                Checked = true
            };

            _rbMultiRow = new RadioButton
            {
                Text = "Overlay Rows",
                Left = 520,
                Top = 35
            };

            _lblRow = new System.Windows.Forms.Label
            {
                Text = "Row:",
                Left = 620,
                Top = 14,
                AutoSize = true
            };

            _nudRow = new NumericUpDown
            {
                Left = 660,
                Top = 10,
                Width = 80,
                Minimum = 0,
                Maximum = 0
            };

            _lblOverlays = new System.Windows.Forms.Label
            {
                Text = "# overlays:",
                Left = 750,
                Top = 14,
                AutoSize = true
            };

            _nudOverlayCount = new NumericUpDown
            {
                Left = 830,
                Top = 10,
                Width = 60,
                Minimum = 1,
                Maximum = 20,
                Value = 5
            };

            // Prev / Next buttons for navigation
            _btnPrev = new Button
            {
                Text = "<",
                Left = 900,
                Top = 8,
                Width = 40
            };
            _btnPrev.Click += (s, e) => StepRows(-1);

            _btnNext = new Button
            {
                Text = ">",
                Left = 945,
                Top = 8,
                Width = 40
            };
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

            // --- Charts stacked vertically with shared X-axis behavior ---
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

            var rightPanel = new Panel
            {
                Dock = DockStyle.Fill
            };
            rightPanel.Controls.Add(chartsLayout);
            rightPanel.Controls.Add(topPanel);

            Controls.Add(rightPanel);
            Controls.Add(_dgvChannels);


        }

        // =========================
        // X-axis synchronization
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
        // DGV event handler: keep ChannelData in sync and stable
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
        // Top-level plotting based on UI state
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
            {
                PlotSingleRow(baseRow);
            }
            else
            {
                int overlays = (int)_nudOverlayCount.Value;
                PlotRowOverlay(baseRow, overlays);
            }

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

            int channelIndex = 0;
            foreach (string filePath in ofd.FileNames)
            {
                if (channelIndex >= MAX_CHANNELS)
                {
                    MessageBox.Show($"Exceeded MAX_CHANNELS ({MAX_CHANNELS}). Extra files will be ignored.",
                        "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                }

                var ch = LoadChannelFromCsv(filePath, channelIndex);
                _channels.Add(ch);
                channelIndex++;
            }

            if (_channels.Count == 0)
                return;

            // Make sure all channels have same shape
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

            // Update numeric ranges
            if (_numRows > 0)
            {
                _nudRow.Maximum = _numRows - 1;
            }

            // First plot will auto-scale when user hits Plot
        }

        private void BtnSaveConfig_Click(object? sender, EventArgs e)
        {
            SaveCurrentConfig();
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

            foreach (var ch in _channels)
            {
                int chIndex = _channels.IndexOf(ch);
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

                foreach (var ch in _channels)
                {
                    int chIndex = _channels.IndexOf(ch);
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
        // CSV loading
        // =========================

        private ChannelData LoadChannelFromCsv(string filePath, int channelIndex)
        {
            var lines = File.ReadAllLines(filePath);

            // Skip completely empty lines
            var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (nonEmpty.Length <= 1)
                throw new InvalidOperationException("CSV has no data rows (after header): " + filePath);

            // First non-empty line is header -> ignore
            var dataLines = nonEmpty.Skip(1).ToArray();
            int numRows = dataLines.Length;

            // Ignore the first two columns (Timestamp, Raw Timestamp)
            // Use minimum sample count (after skipping first 2) across all rows
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
                    int idx = 2 + c; // skip first 2 columns (timestamps)
                    double val = 0.0;
                    if (idx < parts.Length)
                    {
                        string token = parts[idx].Trim();
                        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                        {
                            val = 0.0;
                        }
                    }
                    data[r, c] = val;
                }
            }

            string name = Path.GetFileNameWithoutExtension(filePath);

            // Apply config if available, else defaults
            double bias = 0.0;
            double scale = 1.0;
            bool show1 = true;
            bool show2 = false;

            var cfgEntry = _config.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            if (cfgEntry != null)
            {
                bias = cfgEntry.Bias;
                scale = cfgEntry.Scale;
                show1 = cfgEntry.ShowOnChart1;
                show2 = cfgEntry.ShowOnChart2;
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
                Name = name,
                Data = data,
                Bias = bias,
                Scale = scale,
                ShowOnChart1 = show1,
                ShowOnChart2 = show2
            };
        }

        // =========================
        // Config handling
        // =========================

        private void LoadConfigIfExists()
        {
            if (!File.Exists(CONFIG_FILE_NAME))
            {
                _config = new AppConfig
                {
                    SampleIntervalSec = DEFAULT_SAMPLE_INTERVAL_SEC
                };
                _txtSampleInterval.Text = DEFAULT_SAMPLE_INTERVAL_SEC.ToString("G6", CultureInfo.InvariantCulture);
                return;
            }

            try
            {
                string json = File.ReadAllText(CONFIG_FILE_NAME);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    _config = cfg;
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
            _config.Channels.Clear();

            foreach (var ch in _channels)
            {
                _config.Channels.Add(new ChannelConfigEntry
                {
                    Name = ch.Name,
                    Bias = ch.Bias,
                    Scale = ch.Scale,
                    ShowOnChart1 = ch.ShowOnChart1,
                    ShowOnChart2 = ch.ShowOnChart2
                });
            }

            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(CONFIG_FILE_NAME, json);
            MessageBox.Show("Config saved.", "Config",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // =========================
    // Helper classes
    // =========================

    public class ChannelData
    {
        public string Name { get; set; } = string.Empty;
        public double[,] Data { get; set; } = new double[0, 0];
        public double Bias { get; set; }
        public double Scale { get; set; }
        public bool ShowOnChart1 { get; set; } = true;
        public bool ShowOnChart2 { get; set; } = false;
    }

    public class ChannelConfigEntry
    {
        public string Name { get; set; } = string.Empty;
        public double Bias { get; set; }
        public double Scale { get; set; }
        public bool ShowOnChart1 { get; set; } = true;
        public bool ShowOnChart2 { get; set; } = false;
    }

    public class AppConfig
    {
        public double SampleIntervalSec { get; set; }
        public System.Collections.Generic.List<ChannelConfigEntry> Channels { get; set; } = new();
    }
}
