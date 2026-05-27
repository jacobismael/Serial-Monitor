using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace serial.Desktop;

public partial class SerialPlotterControl : UserControl
{
    private readonly List<PlotSample> _history = [];
    private readonly Dictionary<string, IBrush> _seriesBrushes = [];
    private SerialPlotterSettings _settings = new();
    private DateTime? _lastRxAt;
    private DateTime? _lastParsedAt;
    private DateTime? _lastIgnoredAt;
    private DateTime? _lastExportAt;
    private string? _serialPort;
    private int? _serialBaudRate;
    private long _rxBytes;
    private int _rxLines;
    private int _parsedLines;
    private int _ignoredLines;
    private bool _isPaused;
    private bool _isStatusVisible = true;
    private string _parserStatus = "Waiting for numeric data";
    private string _lastParsed = "-";
    private string _lastIgnored = "-";
    private string _lastError = "None";

    public SerialPlotterControl()
    {
        InitializeComponent();

        XAxisModeComboBox.ItemsSource = new[] { "Samples", "Time" };
        XAxisModeComboBox.SelectedItem = _settings.XAxisMode;

        PauseResumeButton.Click += (_, _) => TogglePause();
        AutoYButton.Click += (_, _) =>
        {
            _settings.AutoScale = true;
            Redraw();
        };
        FitViewButton.Click += (_, _) => Redraw();
        ResetZoomButton.Click += (_, _) => Redraw();
        ClearPlotButton.Click += (_, _) => ClearPlot();
        ClearHistoryButton.Click += (_, _) => ClearHistory();
        ExportVisibleButton.Click += async (_, _) => await ExportAsync(visibleOnly: true);
        ExportFullButton.Click += async (_, _) => await ExportAsync(visibleOnly: false);
        XAxisModeComboBox.SelectionChanged += (_, _) =>
        {
            _settings.XAxisMode = XAxisModeComboBox.SelectedItem as string ?? "Samples";
            Redraw();
        };
        PlotCanvas.SizeChanged += (_, _) => Redraw();
        ConfigureStatusPanelContextMenu();

        UpdateStatusPanel();
        Redraw();
    }

    public void UpdateSettings(SerialPlotterSettings settings)
    {
        settings.Normalize();
        _settings = settings;
        _seriesBrushes.Clear();
        XAxisModeComboBox.SelectedItem = _settings.XAxisMode;
        TrimHistory();
        Redraw();
    }

    public void UpdateSerialConnection(string? portName, int? baudRate)
    {
        _serialPort = portName;
        _serialBaudRate = baudRate;
        UpdateStatusPanel();
    }

    public void ProcessSerialData(string data)
    {
        DateTime timestamp = DateTime.Now;
        _lastRxAt = timestamp;
        _rxLines++;
        _rxBytes += Encoding.UTF8.GetByteCount(data);

        if (_isPaused)
        {
            _parserStatus = "Paused";
            UpdateStatusPanel();
            return;
        }

        IReadOnlyList<(string Name, double Value)> values = ParseValues(data);
        if (values.Count == 0)
        {
            _ignoredLines++;
            _lastIgnoredAt = timestamp;
            _lastIgnored = data.Trim();
            _parserStatus = "Ignored non-numeric data";
            UpdateStatusPanel();
            return;
        }

        int sampleIndex = _history.Count == 0 ? 0 : _history[^1].SampleIndex + 1;
        foreach ((string name, double value) in values)
        {
            _history.Add(new PlotSample(timestamp, sampleIndex, name, value));
            EnsureSeriesBrush(name);
        }

        _parsedLines++;
        _lastParsedAt = timestamp;
        _lastParsed = string.Join(", ", values.Select(value => $"{value.Name}={value.Value:0.###}"));
        _parserStatus = "Parsed numeric data";
        TrimHistory();
        Redraw();
    }

    private IReadOnlyList<(string Name, double Value)> ParseValues(string data)
    {
        string text = data.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        List<(string Name, double Value)> values = [];
        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            string name = parts.Length > 1 ? $"series{i}" : "value";
            string valueText = part;
            int separatorIndex = part.IndexOf('=');
            if (separatorIndex < 0)
            {
                separatorIndex = part.IndexOf(':');
            }

            if (separatorIndex >= 0 && separatorIndex < part.Length - 1)
            {
                name = NormalizeSeriesName(part[..separatorIndex], i);
                valueText = part[(separatorIndex + 1)..].Trim();
            }

            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }

            values.Add((name, value));
        }

        return values;
    }

    private static string NormalizeSeriesName(string name, int fallbackIndex)
    {
        string normalized = name.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? $"series{fallbackIndex}" : normalized;
    }

    private void TogglePause()
    {
        _isPaused = !_isPaused;
        PauseResumeButton.Content = _isPaused ? "Resume" : "Pause";
        _parserStatus = _isPaused ? "Paused" : "Waiting for numeric data";
        UpdateStatusPanel();
    }

    public void ShowStatusPanel()
    {
        SetStatusPanelVisible(true);
    }

    private void HideStatusPanel()
    {
        SetStatusPanelVisible(false);
    }

    private void SetStatusPanelVisible(bool isVisible)
    {
        _isStatusVisible = isVisible;
        PlotterStatusPanel.IsVisible = _isStatusVisible;
        if (PlotterRootGrid.ColumnDefinitions.Count > 1)
        {
            PlotterRootGrid.ColumnDefinitions[1].Width = _isStatusVisible ? new GridLength(340) : new GridLength(0);
        }

        PlotterRootGrid.ColumnSpacing = _isStatusVisible ? 12 : 0;
    }

    private void ConfigureStatusPanelContextMenu()
    {
        MenuItem showRootItem = new()
        {
            Header = "Show Plotter Status"
        };
        MenuItem hideRootItem = new()
        {
            Header = "Hide Plotter Status"
        };
        MenuItem hideItem = new()
        {
            Header = "Hide Plotter Status"
        };
        showRootItem.Click += (_, _) => ShowStatusPanel();
        hideRootItem.Click += (_, _) => HideStatusPanel();
        hideItem.Click += (_, _) => HideStatusPanel();

        ContextMenu rootContextMenu = new()
        {
            Items =
            {
                showRootItem,
                hideRootItem
            }
        };
        PlotterRootGrid.ContextMenu = rootContextMenu;
        MenuItem showCanvasItem = new()
        {
            Header = "Show Plotter Status"
        };
        MenuItem hideCanvasItem = new()
        {
            Header = "Hide Plotter Status"
        };
        showCanvasItem.Click += (_, _) => ShowStatusPanel();
        hideCanvasItem.Click += (_, _) => HideStatusPanel();

        PlotCanvas.ContextMenu = new ContextMenu
        {
            Items =
            {
                showCanvasItem,
                hideCanvasItem
            }
        };

        PlotterStatusPanel.ContextMenu = new ContextMenu
        {
            Items =
            {
                hideItem
            }
        };
    }

    private void ClearPlot()
    {
        int visibleSamples = Math.Max(1, _settings.VisibleSamples);
        int minIndex = Math.Max(0, (_history.Select(sample => sample.SampleIndex).DefaultIfEmpty(0).Max() - visibleSamples) + 1);
        _history.RemoveAll(sample => sample.SampleIndex >= minIndex);
        Redraw();
    }

    private void ClearHistory()
    {
        _history.Clear();
        _seriesBrushes.Clear();
        _parsedLines = 0;
        _ignoredLines = 0;
        _lastParsed = "-";
        _lastIgnored = "-";
        _parserStatus = "Waiting for numeric data";
        Redraw();
    }

    private void TrimHistory()
    {
        if (_settings.SaveFullHistory)
        {
            int maxHistory = Math.Max(_settings.VisibleSamples, _settings.MaxHistorySamples);
            if (_history.Count > maxHistory)
            {
                _history.RemoveRange(0, _history.Count - maxHistory);
            }

            return;
        }

        int maxVisible = Math.Max(10, _settings.VisibleSamples);
        int maxIndex = _history.Select(sample => sample.SampleIndex).DefaultIfEmpty(0).Max();
        int minIndex = Math.Max(0, maxIndex - maxVisible + 1);
        _history.RemoveAll(sample => sample.SampleIndex < minIndex);
    }

    private List<PlotSample> GetVisibleSamples()
    {
        int visibleSamples = Math.Max(10, _settings.VisibleSamples);
        int maxIndex = _history.Select(sample => sample.SampleIndex).DefaultIfEmpty(0).Max();
        int minIndex = Math.Max(0, maxIndex - visibleSamples + 1);
        return _history.Where(sample => sample.SampleIndex >= minIndex).ToList();
    }

    private void Redraw()
    {
        PlotCanvas.Children.Clear();

        double width = Math.Max(320, PlotCanvas.Bounds.Width);
        double height = Math.Max(240, PlotCanvas.Bounds.Height);
        double left = 58;
        double top = 18;
        double right = 14;
        double bottom = 34;
        Rect plotRect = new(left, top, Math.Max(1, width - left - right), Math.Max(1, height - top - bottom));

        List<PlotSample> visibleSamples = GetVisibleSamples();
        DrawGridAndAxes(plotRect, visibleSamples);

        if (visibleSamples.Count == 0)
        {
            DrawEmptyState(plotRect);
            UpdateStatusPanel();
            return;
        }

        double min = _settings.AutoScale ? visibleSamples.Min(sample => sample.Value) : _settings.MinimumValue;
        double max = _settings.AutoScale ? visibleSamples.Max(sample => sample.Value) : _settings.MaximumValue;
        if (Math.Abs(max - min) < 0.000001)
        {
            min -= 1;
            max += 1;
        }

        int minIndex = visibleSamples.Min(sample => sample.SampleIndex);
        int maxIndex = visibleSamples.Max(sample => sample.SampleIndex);
        int xSpan = Math.Max(1, maxIndex - minIndex);
        string[] seriesNames = visibleSamples.Select(sample => sample.SeriesName).Distinct(StringComparer.Ordinal).ToArray();

        foreach (string seriesName in seriesNames)
        {
            List<PlotSample> series = visibleSamples
                .Where(sample => sample.SeriesName == seriesName)
                .OrderBy(sample => sample.SampleIndex)
                .ToList();
            if (series.Count == 0)
            {
                continue;
            }

            IBrush brush = EnsureSeriesBrush(seriesName);
            Point? previous = null;
            foreach (PlotSample sample in series)
            {
                double x = plotRect.X + ((sample.SampleIndex - minIndex) / (double)xSpan * plotRect.Width);
                double normalized = (sample.Value - min) / (max - min);
                double y = plotRect.Bottom - (Math.Clamp(normalized, 0, 1) * plotRect.Height);
                Point point = new(x, y);

                if (previous.HasValue)
                {
                    PlotCanvas.Children.Add(new Line
                    {
                        StartPoint = previous.Value,
                        EndPoint = point,
                        Stroke = brush,
                        StrokeThickness = Math.Clamp(_settings.LineThickness, 0.5, 6)
                    });
                }

                if (_settings.ShowPoints)
                {
                    PlotCanvas.Children.Add(new Ellipse
                    {
                        Width = 4,
                        Height = 4,
                        Fill = brush,
                        Margin = new Thickness(point.X - 2, point.Y - 2, 0, 0)
                    });
                }

                previous = point;
            }
        }

        if (seriesNames.Length > 1)
        {
            DrawLegend(seriesNames, plotRect);
        }

        UpdateStatusPanel();
    }

    private void DrawGridAndAxes(Rect plotRect, IReadOnlyList<PlotSample> visibleSamples)
    {
        Pen gridPen = new(Brushes.DimGray, 0.5);
        Pen axisPen = new(Brushes.Gray, 0.8);

        if (_settings.ShowGrid)
        {
            for (int i = 0; i <= 8; i++)
            {
                double x = plotRect.X + (plotRect.Width * i / 8);
                PlotCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(x, plotRect.Y),
                    EndPoint = new Point(x, plotRect.Bottom),
                    Stroke = gridPen.Brush,
                    StrokeThickness = gridPen.Thickness
                });
            }

            for (int i = 0; i <= 5; i++)
            {
                double y = plotRect.Y + (plotRect.Height * i / 5);
                PlotCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(plotRect.X, y),
                    EndPoint = new Point(plotRect.Right, y),
                    Stroke = gridPen.Brush,
                    StrokeThickness = gridPen.Thickness
                });
            }
        }

        PlotCanvas.Children.Add(new Line
        {
            StartPoint = new Point(plotRect.X, plotRect.Bottom),
            EndPoint = new Point(plotRect.Right, plotRect.Bottom),
            Stroke = axisPen.Brush,
            StrokeThickness = axisPen.Thickness
        });
        PlotCanvas.Children.Add(new Line
        {
            StartPoint = new Point(plotRect.X, plotRect.Y),
            EndPoint = new Point(plotRect.X, plotRect.Bottom),
            Stroke = axisPen.Brush,
            StrokeThickness = axisPen.Thickness
        });

        double min = visibleSamples.Count == 0
            ? _settings.MinimumValue
            : _settings.AutoScale ? visibleSamples.Min(sample => sample.Value) : _settings.MinimumValue;
        double max = visibleSamples.Count == 0
            ? _settings.MaximumValue
            : _settings.AutoScale ? visibleSamples.Max(sample => sample.Value) : _settings.MaximumValue;
        if (Math.Abs(max - min) < 0.000001)
        {
            min -= 1;
            max += 1;
        }

        for (int i = 0; i <= 5; i++)
        {
            double value = max - ((max - min) * i / 5);
            double y = plotRect.Y + (plotRect.Height * i / 5);
            DrawText($"{value:0.###}", new Point(4, y - 8), Brushes.Gray, 11);
        }

        int minIndex = visibleSamples.Select(sample => sample.SampleIndex).DefaultIfEmpty(0).Min();
        int maxIndex = visibleSamples.Select(sample => sample.SampleIndex).DefaultIfEmpty(0).Max();
        for (int i = 0; i <= 4; i++)
        {
            double x = plotRect.X + (plotRect.Width * i / 4);
            string label = _settings.XAxisMode == "Time" && visibleSamples.Count > 0
                ? FormatRelativeSampleTime(visibleSamples, i / 4.0)
                : Math.Round(minIndex + ((maxIndex - minIndex) * i / 4.0)).ToString(CultureInfo.InvariantCulture);
            DrawText(label, new Point(x - 10, plotRect.Bottom + 10), Brushes.Gray, 11);
        }
    }

    private static string FormatRelativeSampleTime(IReadOnlyList<PlotSample> samples, double position)
    {
        DateTime first = samples[0].Timestamp;
        DateTime last = samples[^1].Timestamp;
        TimeSpan elapsed = last - first;
        double seconds = elapsed.TotalSeconds * position;
        return seconds < 1 ? $"{seconds * 1000:0.#} ms" : $"{seconds:0.##} s";
    }

    private void DrawEmptyState(Rect plotRect)
    {
        string text = "Waiting for numeric serial data.\nAccepted formats:\n  1.23\n  Value: 1.23\n  voltage=3.28\n  temp: 24.1, voltage: 3.30\n  1.23,2.34,3.45";
        DrawText(text, new Point(plotRect.X + 18, plotRect.Y + 18), Brushes.Gray, 13);
    }

    private void DrawLegend(IReadOnlyList<string> seriesNames, Rect plotRect)
    {
        double x = plotRect.Right - 150;
        double y = plotRect.Y + 10;
        for (int i = 0; i < seriesNames.Count; i++)
        {
            string name = seriesNames[i];
            IBrush brush = EnsureSeriesBrush(name);
            PlotCanvas.Children.Add(new Line
            {
                StartPoint = new Point(x, y + 7),
                EndPoint = new Point(x + 18, y + 7),
                Stroke = brush,
                StrokeThickness = 2
            });
            DrawText(name, new Point(x + 24, y), Brushes.White, 11);
            y += 18;
            if (i >= 7)
            {
                break;
            }
        }
    }

    private IBrush EnsureSeriesBrush(string seriesName)
    {
        if (_seriesBrushes.TryGetValue(seriesName, out IBrush? brush))
        {
            return brush;
        }

        string[] colors = _settings.SeriesColors.Count > 0
            ? _settings.SeriesColors.ToArray()
            : [_settings.LineColor];
        brush = CreateBrush(colors[_seriesBrushes.Count % colors.Length]);
        _seriesBrushes[seriesName] = brush;
        return brush;
    }

    private void UpdateStatusPanel()
    {
        if (!IsInitialized)
        {
            return;
        }

        List<PlotSample> visibleSamples = GetVisibleSamples();
        string state = _isPaused ? "Paused" : _history.Count == 0 ? "No Data" : "Running";
        string yRange = _settings.AutoScale ? "Auto" : "Manual";
        string latestValue = visibleSamples.Count == 0 ? "-" : $"{visibleSamples[^1].SeriesName}={visibleSamples[^1].Value:0.###}";
        string min = visibleSamples.Count == 0 ? "-" : visibleSamples.Min(sample => sample.Value).ToString("0.###", CultureInfo.InvariantCulture);
        string max = visibleSamples.Count == 0 ? "-" : visibleSamples.Max(sample => sample.Value).ToString("0.###", CultureInfo.InvariantCulture);
        string average = visibleSamples.Count == 0 ? "-" : visibleSamples.Average(sample => sample.Value).ToString("0.###", CultureInfo.InvariantCulture);
        string sampleRate = EstimateSampleRate(visibleSamples);
        string connection = string.IsNullOrWhiteSpace(_serialPort) ? "Disconnected" : "Connected";

        StringBuilder builder = new();
        AppendSection(builder, "PLOTTER");
        AppendRow(builder, "State", state);
        AppendRow(builder, "Visible Samples", $"{visibleSamples.Count} / {_settings.VisibleSamples}");
        AppendRow(builder, "Total Samples", _history.Count.ToString("N0", CultureInfo.InvariantCulture));
        AppendRow(builder, "Series Count", _history.Select(sample => sample.SeriesName).Distinct().Count().ToString(CultureInfo.InvariantCulture));
        AppendRow(builder, "X Axis", _settings.XAxisMode);
        AppendRow(builder, "Y Range", yRange);
        AppendRow(builder, "Latest Value", latestValue);
        AppendRow(builder, "Min", min);
        AppendRow(builder, "Max", max);
        AppendRow(builder, "Average", average);
        AppendRow(builder, "Sample Rate", sampleRate);
        builder.AppendLine();

        AppendSection(builder, "SERIAL");
        AppendRow(builder, "Connection", connection);
        AppendRow(builder, "Port", _serialPort ?? "-");
        AppendRow(builder, "Baud Rate", _serialBaudRate?.ToString(CultureInfo.InvariantCulture) ?? "-");
        AppendRow(builder, "Last RX", FormatRelativeTime(_lastRxAt));
        AppendRow(builder, "RX Lines", _rxLines.ToString("N0", CultureInfo.InvariantCulture));
        AppendRow(builder, "RX Bytes", _rxBytes.ToString("N0", CultureInfo.InvariantCulture));
        builder.AppendLine();

        AppendSection(builder, "PARSER");
        AppendRow(builder, "Status", _parserStatus);
        AppendRow(builder, "Parsed Lines", _parsedLines.ToString("N0", CultureInfo.InvariantCulture));
        AppendRow(builder, "Ignored Lines", _ignoredLines.ToString("N0", CultureInfo.InvariantCulture));
        AppendRow(builder, "Last Parsed", _lastParsed);
        AppendRow(builder, "Last Ignored", _lastIgnored);
        AppendRow(builder, "Format", _settings.ParserMode);
        builder.AppendLine();

        AppendSection(builder, "EXPORT");
        AppendRow(builder, "Mode", _settings.CsvExportMode);
        AppendRow(builder, "Visible Buffer", $"{_settings.VisibleSamples} samples");
        AppendRow(builder, "History Enabled", _settings.SaveFullHistory ? "On" : "Off");
        AppendRow(builder, "Last Export", FormatRelativeTime(_lastExportAt));
        AppendRow(builder, "Last Error", _lastError);

        PlotterStatusTextBlock.Text = builder.ToString();
    }

    private static string EstimateSampleRate(IReadOnlyList<PlotSample> visibleSamples)
    {
        if (visibleSamples.Count < 2)
        {
            return "-";
        }

        TimeSpan duration = visibleSamples[^1].Timestamp - visibleSamples[0].Timestamp;
        if (duration.TotalSeconds <= 0)
        {
            return "-";
        }

        return $"{(visibleSamples.Count - 1) / duration.TotalSeconds:0.#} Hz";
    }

    private async System.Threading.Tasks.Task ExportAsync(bool visibleOnly)
    {
        List<PlotSample> samples = visibleOnly ? GetVisibleSamples() : _history.ToList();
        if (samples.Count == 0)
        {
            _lastError = "No samples to export.";
            UpdateStatusPanel();
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            _lastError = "Could not open save dialog.";
            UpdateStatusPanel();
            return;
        }

        string scope = visibleOnly ? "visible" : "full";
        FilePickerSaveOptions saveOptions = new()
        {
            Title = $"Export {scope} serial plotter CSV",
            SuggestedFileName = $"serial-plotter-{scope}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV files")
                {
                    Patterns = ["*.csv"]
                }
            ]
        };

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(saveOptions);
        if (file == null)
        {
            return;
        }

        try
        {
            await using Stream stream = await file.OpenWriteAsync();
            using StreamWriter writer = new(stream);
            await writer.WriteLineAsync("timestamp,sample_index,series_name,value");
            foreach (PlotSample sample in samples)
            {
                await writer.WriteLineAsync(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:O},{1},{2},{3}",
                    sample.Timestamp,
                    sample.SampleIndex,
                    EscapeCsv(sample.SeriesName),
                    sample.Value));
            }

            _lastExportAt = DateTime.Now;
            _lastError = "None";
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }

        UpdateStatusPanel();
    }

    private static string EscapeCsv(string value)
    {
        return value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static IBrush CreateBrush(string colorText)
    {
        string value = string.IsNullOrWhiteSpace(colorText) ? "#00D4D8" : colorText.Trim();
        return TryParseHexColor(value, out Color color)
            ? new SolidColorBrush(color)
            : Brushes.Cyan;
    }

    private static bool TryParseHexColor(string? colorText, out Color color)
    {
        color = default;
        string value = (colorText ?? "").Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value[1..];
        }

        if (value.Length != 6 && value.Length != 8)
        {
            return false;
        }

        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
        {
            return false;
        }

        color = value.Length == 6
            ? Color.FromRgb(
                (byte)((parsed >> 16) & 0xff),
                (byte)((parsed >> 8) & 0xff),
                (byte)(parsed & 0xff))
            : Color.FromArgb(
                (byte)((parsed >> 24) & 0xff),
                (byte)((parsed >> 16) & 0xff),
                (byte)((parsed >> 8) & 0xff),
                (byte)(parsed & 0xff));
        return true;
    }

    private void DrawText(string text, Point point, IBrush brush, double fontSize, FontWeight? weight = null)
    {
        TextBlock textBlock = new()
        {
            Text = text,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = fontSize,
            Foreground = brush,
            FontWeight = weight ?? FontWeight.Normal,
            TextWrapping = TextWrapping.NoWrap
        };
        Canvas.SetLeft(textBlock, point.X);
        Canvas.SetTop(textBlock, point.Y);
        PlotCanvas.Children.Add(textBlock);
    }

    private static void AppendSection(StringBuilder builder, string title)
    {
        builder.Append(title);
        builder.Append(' ');
        builder.AppendLine(new string('-', Math.Max(1, 32 - title.Length)));
    }

    private static void AppendRow(StringBuilder builder, string label, string value)
    {
        builder.Append(label.PadRight(16));
        builder.Append(": ");
        builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static string FormatRelativeTime(DateTime? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return "-";
        }

        TimeSpan elapsed = DateTime.Now - timestamp.Value;
        if (elapsed.TotalSeconds < 2)
        {
            return "just now";
        }

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00} ago"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00} ago";
    }

    private sealed record PlotSample(DateTime Timestamp, int SampleIndex, string SeriesName, double Value);
}
