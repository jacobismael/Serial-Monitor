using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using serial.Core;

namespace serial.Desktop;

public partial class LogicAnalyzerControl : UserControl, IDisposable
{
    private static readonly JsonSerializerOptions CaptureJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly int[] _baudRates = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 2_000_000];
    private readonly LogicAnalyzerParser _parser = new();
    private SerialPort? _serialPort;
    private LogicCapture? _currentCapture;
    private LogicAnalyzerSettings _settings = new();
    private readonly StringBuilder _probeTextBuffer = new();
    private DateTime? _connectedAt;
    private DateTime? _lastPacketAt;
    private DateTime? _lastCaptureAt;
    private DateTime? _lastDecodeAt;
    private string? _lastSeenPort;
    private string _probeConnectionState = "Disconnected";
    private string _probeId = "-";
    private string _firmwareVersion = "-";
    private string _bitstreamVersion = "-";
    private string _fpgaClock = "-";
    private string _inputVoltage = "-";
    private string _probeChannelCount = "-";
    private string _maxSampleRate = "-";
    private string _captureDepth = "-";
    private long _probeRxBytes;
    private int _crcErrors;
    private int _lastPacketBytes;
    private string _probeCaptureState = "Idle";
    private string _probeParserState = "Idle";
    private string _decodeStatus = "No capture loaded";
    private string _recordTimeUnit = "ms";
    private string _capturePlanStatus = "Ready";
    private int _decodedFrames;
    private int _decodeErrors;
    private string _probeLastError = "None";
    private bool _isCapturing;
    private bool _showProbeStatus = true;
    private bool _showProtocolAnalyzer = true;
    private bool _syncingRecordTimeUnit;
    private GridLength _savedProtocolRowHeight = new(220);
    private readonly List<TriggerConditionRowControls> _triggerConditionRows = [];
    private readonly List<LogicCapture> _captureHistory = [];
    private readonly List<LogicSignalDefinition> _signalDefinitions = [];
    private readonly List<LogicBusDefinition> _busDefinitions = [];
    private LogicTriggerExpression _triggerExpression = new();
    private LogicQualificationMode _qualificationMode = LogicQualificationMode.StoreAll;
    private string _qualificationConditionText = "";
    private int _qualificationStoredSamples;
    private int _qualificationSkippedSamples;
    private string _qualificationTimingStatus = "Continuous";
    private int? _matchedTriggerSample;
    private int? _cursorASample;
    private int? _cursorBSample;
    private int _captureHistoryIndex = -1;
    private bool _isAutoCaptureRunning;
    private int _autoCaptureLimit;
    private int _autoCapturesCompleted;
    private string _autoCaptureState = "Off";
    private string _selectedSignal = "-";
    private string _captureFileName = "-";
    private bool _captureFileSaved = true;
    private DateTime? _lastSavedAt;

    public event Action? ConnectionSettingsChanged;

    public IReadOnlyList<int> BaudRates => _baudRates;

    public bool IsConnected => _serialPort?.IsOpen == true;

    public string? SelectedPort
    {
        get => PortComboBox.SelectedItem as string;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                PortComboBox.SelectedItem = value;
            }
        }
    }

    public int SelectedBaudRate
    {
        get => BaudComboBox.SelectedItem is int selected ? selected : 115200;
        set
        {
            if (_baudRates.Contains(value))
            {
                BaudComboBox.SelectedItem = value;
            }
        }
    }

    public LogicAnalyzerControl()
    {
        InitializeComponent();

        _crcErrors = 0;
        BaudComboBox.ItemsSource = _baudRates;
        BaudComboBox.SelectedItem = 115200;
        ChannelCountComboBox.ItemsSource = new[] { 8, 16 };
        ChannelCountComboBox.SelectedItem = 8;
        TriggerModeComboBox.ItemsSource = Enum.GetNames<LogicTriggerMode>();
        TriggerModeComboBox.SelectedItem = nameof(LogicTriggerMode.None);
        TriggerBuilderModeComboBox.ItemsSource = new[] { "Basic", "Advanced" };
        TriggerBuilderModeComboBox.SelectedItem = "Basic";
        TriggerCombineComboBox.ItemsSource = new[] { "AND", "OR" };
        TriggerCombineComboBox.SelectedItem = "AND";
        QualificationModeComboBox.ItemsSource = new[] { "Store All Samples", "Store When Condition True" };
        QualificationModeComboBox.SelectedItem = "Store All Samples";
        AutoCaptureLimitComboBox.ItemsSource = new[] { "Infinite", "1", "5", "10", "100" };
        AutoCaptureLimitComboBox.SelectedItem = "Infinite";
        RecordTimeUnitComboBox.ItemsSource = new[] { "µs", "ms", "s" };
        RecordTimeUnitComboBox.SelectedItem = _recordTimeUnit;
        MockTypeComboBox.ItemsSource = new[] { "General", "UART", "I2C", "SPI", "CAN" };
        MockTypeComboBox.SelectedItem = "General";

        RefreshPortsButton.Click += (_, _) => RefreshPorts();
        ConnectButton.Click += (_, _) => ToggleConnection();
        StartCaptureButton.Click += (_, _) => SendCaptureCommand("START");
        StopCaptureButton.Click += (_, _) => SendStopCommand();
        SingleCaptureButton.Click += (_, _) => SendCaptureCommand("SINGLE");
        StartAutoCaptureButton.Click += (_, _) => StartAutoCapture();
        StopAutoCaptureButton.Click += (_, _) => StopAutoCapture();
        ClearCaptureButton.Click += (_, _) => ClearCapture();
        GenerateMockButton.Click += (_, _) => LoadCapture(CreateMockCapture());
        FitViewButton.Click += (_, _) => WaveformView.FitView();
        ExportCsvButton.Click += async (_, _) => await ExportCaptureAsync("csv");
        ExportVcdButton.Click += async (_, _) => await ExportCaptureAsync("vcd");
        PreviousCaptureButton.Click += (_, _) => ShowCaptureAt(_captureHistoryIndex - 1);
        NextCaptureButton.Click += (_, _) => ShowCaptureAt(_captureHistoryIndex + 1);
        ChannelCountComboBox.SelectionChanged += (_, _) => ConfigureTriggerChannels();
        TriggerModeComboBox.SelectionChanged += (_, _) => UpdateProbeFpgaStatus();
        TriggerChannelComboBox.SelectionChanged += (_, _) => UpdateProbeFpgaStatus();
        SampleRateNumericUpDown.ValueChanged += (_, _) => UpdateProbeFpgaStatus();
        RecordTimeNumericUpDown.ValueChanged += (_, _) => UpdateProbeFpgaStatus();
        RecordTimeUnitComboBox.SelectionChanged += (_, _) => HandleRecordTimeUnitChanged();
        TriggerPositionNumericUpDown.ValueChanged += (_, _) => ApplyTriggerToCurrentCapture();
        AddTriggerConditionButton.Click += (_, _) => AddTriggerConditionRow();
        ClearTriggerButton.Click += (_, _) => ClearTriggerBuilder();
        ApplyTriggerButton.Click += (_, _) => ApplyTriggerToCurrentCapture();
        TriggerBuilderModeComboBox.SelectionChanged += (_, _) => ApplyTriggerBuilderMode();
        TriggerCombineComboBox.SelectionChanged += (_, _) => UpdateProbeFpgaStatus();
        QualificationModeComboBox.SelectionChanged += (_, _) => UpdateQualificationState();
        QualificationConditionTextBox.TextChanged += (_, _) => UpdateQualificationState();
        AutoCaptureLimitComboBox.SelectionChanged += (_, _) => UpdateAutoCaptureState();
        CursorAButton.Click += (_, _) => SetCursorFromCurrentSample(isCursorA: true);
        CursorBButton.Click += (_, _) => SetCursorFromCurrentSample(isCursorA: false);
        ClearCursorsButton.Click += (_, _) => ClearCursors();
        WaveformView.CursorChanged += text => CursorTextBlock.Text = text;
        WaveformView.MeasurementsChanged += (cursorA, cursorB) =>
        {
            _cursorASample = cursorA;
            _cursorBSample = cursorB;
            UpdateProbeFpgaStatus();
        };
        ProtocolAnalyzerPanel.FramesChanged += frames => WaveformView.SetProtocolFrames(frames);
        ProtocolAnalyzerPanel.FrameSelected += frame => WaveformView.CenterOnFrame(frame);
        ProtocolAnalyzerPanel.StatusChanged += UpdateProtocolStatus;
        ConfigurePanelContextMenus();

        ConfigureTriggerChannels();
        ConfigureSignalDefinitions(GetChannelCount());
        AddTriggerConditionRow();
        ApplyTriggerBuilderMode();
        RefreshPorts();
        SetConnectedUiState(false);
        ApplyPanelVisibility();
    }

    public void UpdateSettings(LogicAnalyzerSettings settings)
    {
        settings.Normalize();
        _settings = settings;

        _showProbeStatus = settings.ShowFpgaStatusPanel;
        _showProtocolAnalyzer = settings.ShowProtocolAnalyzer;
        SampleRateNumericUpDown.Value = settings.DefaultSampleRate;
        RecordTimeNumericUpDown.Value = 10;
        RecordTimeUnitComboBox.SelectedItem = _recordTimeUnit;
        ChannelCountComboBox.SelectedItem = settings.DefaultChannelCount;
        ConfigureTriggerChannels();
        TriggerChannelComboBox.SelectedItem = Math.Clamp(settings.DefaultTriggerChannel, 0, settings.DefaultChannelCount - 1);
        TriggerModeComboBox.SelectedItem = settings.DefaultTriggerMode;
        TriggerPositionNumericUpDown.Value = settings.DefaultTriggerPositionPercent;
        TriggerCombineComboBox.SelectedItem = settings.DefaultTriggerCombineMode;
        QualificationModeComboBox.SelectedItem = settings.DefaultCaptureQualificationMode;
        AutoCaptureLimitComboBox.SelectedItem = settings.DefaultAutoCaptureLimit;
        _autoCaptureState = settings.EnableAutoRetriggerByDefault ? "Ready" : "Off";
        ProtocolAnalyzerPanel.ApplySettings(settings);
        ConfigureSignalDefinitions(GetChannelCount());
        RefreshTriggerConditionRows();
        ApplyPanelVisibility();
        UpdateProbeFpgaStatus();
    }

    public string[] RefreshPorts(bool notify = true)
    {
        string? selectedPort = PortComboBox.SelectedItem as string;
        string[] ports = SerialPort.GetPortNames().OrderBy(port => port).ToArray();
        PortComboBox.ItemsSource = ports;
        if (!string.IsNullOrWhiteSpace(selectedPort) && ports.Contains(selectedPort))
        {
            PortComboBox.SelectedItem = selectedPort;
        }
        else if (ports.Length > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }

        if (notify)
        {
            ConnectionSettingsChanged?.Invoke();
        }

        return ports;
    }

    public void ToggleConnectionFromSettings()
    {
        ToggleConnection();
    }

    private void ConfigureTriggerChannels()
    {
        int channelCount = GetChannelCount();
        int previous = TriggerChannelComboBox.SelectedItem is int selected ? selected : 0;
        int[] channels = Enumerable.Range(0, channelCount).ToArray();
        TriggerChannelComboBox.ItemsSource = channels;
        TriggerChannelComboBox.SelectedItem = channels.Contains(previous) ? previous : 0;
        ConfigureSignalDefinitions(channelCount);
        RefreshTriggerConditionRows();
        WaveformView.SetSignalLabels(GetSignalLabels(channelCount));
        ProtocolAnalyzerPanel.SetSignalLabels(GetSignalLabels(channelCount));
    }

    private void ConfigureSignalDefinitions(int channelCount)
    {
        string[] defaultNames =
        [
            "UART_RX",
            "UART_TX",
            "I2C_SDA",
            "I2C_SCL",
            "SPI_SCLK",
            "SPI_MOSI",
            "SPI_MISO",
            "SPI_CS"
        ];

        for (int channel = 0; channel < channelCount; channel++)
        {
            LogicSignalDefinition? existing = _signalDefinitions.FirstOrDefault(signal => signal.Channel == channel);
            if (existing == null)
            {
                _signalDefinitions.Add(new LogicSignalDefinition
                {
                    Channel = channel,
                    Name = channel < defaultNames.Length ? defaultNames[channel] : $"CH{channel}"
                });
            }
        }

        _signalDefinitions.RemoveAll(signal => signal.Channel >= channelCount);
        if (_busDefinitions.Count == 0 && channelCount >= 4)
        {
            _busDefinitions.Add(new LogicBusDefinition
            {
                Name = "DATA_BUS",
                Channels = [0, 1, 2, 3],
                LsbChannel = 0,
                Radix = _settings.DefaultBusRadix
            });
        }

        WaveformView.SetBusDefinitions(_busDefinitions);
    }

    private string[] GetSignalLabels(int channelCount)
    {
        return Enumerable.Range(0, channelCount)
            .Select(channel =>
            {
                string name = _signalDefinitions.FirstOrDefault(signal => signal.Channel == channel)?.Name ?? $"CH{channel}";
                return string.Equals(name, $"CH{channel}", StringComparison.OrdinalIgnoreCase)
                    ? $"CH{channel}"
                    : $"{name} (CH{channel})";
            })
            .ToArray();
    }

    private string[] GetSignalOptions()
    {
        IEnumerable<string> channelNames = _signalDefinitions
            .OrderBy(signal => signal.Channel)
            .Select(signal => signal.DisplayName);
        IEnumerable<string> busNames = _busDefinitions.Select(bus => bus.DisplayName);
        return channelNames.Concat(busNames).ToArray();
    }

    private void RefreshTriggerConditionRows()
    {
        string[] options = GetSignalOptions();
        foreach (TriggerConditionRowControls row in _triggerConditionRows)
        {
            string? selected = row.SignalComboBox.SelectedItem as string;
            row.SignalComboBox.ItemsSource = options;
            row.SignalComboBox.SelectedItem = options.Contains(selected) ? selected : options.FirstOrDefault();
        }
    }

    private void AddTriggerConditionRow(LogicTriggerCondition? condition = null)
    {
        string[] signalOptions = GetSignalOptions();
        ComboBox signalComboBox = new()
        {
            ItemsSource = signalOptions,
            SelectedItem = condition?.SignalName ?? signalOptions.FirstOrDefault(),
            MinHeight = 34,
            Width = 180
        };
        ComboBox typeComboBox = new()
        {
            ItemsSource = new[] { "Level", "Edge", "BusCompare" },
            SelectedItem = condition?.ConditionType.ToString() ?? "Edge",
            MinHeight = 34,
            Width = 116
        };
        ComboBox operatorComboBox = new()
        {
            ItemsSource = new[] { "==", "!=", "<", "<=", ">", ">=" },
            SelectedItem = condition == null ? "==" : LogicTriggerCondition.FormatOperator(condition.Operator),
            MinHeight = 34,
            Width = 78
        };
        TextBox valueTextBox = new()
        {
            Text = condition?.Value ?? "1",
            PlaceholderText = "0, 1, 0x9F",
            Width = 108
        };
        ComboBox edgeComboBox = new()
        {
            ItemsSource = new[] { "Rising", "Falling", "Either", "NoChange" },
            SelectedItem = condition?.EdgeType.ToString() ?? "Rising",
            MinHeight = 34,
            Width = 116
        };
        Button removeButton = new()
        {
            Content = "Remove",
            MinWidth = 90
        };

        Grid rowGrid = new()
        {
            ColumnSpacing = 8
        };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(180)));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(116)));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(78)));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(108)));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(116)));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90)));
        Grid.SetColumn(signalComboBox, 0);
        Grid.SetColumn(typeComboBox, 1);
        Grid.SetColumn(operatorComboBox, 2);
        Grid.SetColumn(valueTextBox, 3);
        Grid.SetColumn(edgeComboBox, 4);
        Grid.SetColumn(removeButton, 5);
        rowGrid.Children.Add(signalComboBox);
        rowGrid.Children.Add(typeComboBox);
        rowGrid.Children.Add(operatorComboBox);
        rowGrid.Children.Add(valueTextBox);
        rowGrid.Children.Add(edgeComboBox);
        rowGrid.Children.Add(removeButton);

        TriggerConditionRowControls row = new(rowGrid, signalComboBox, typeComboBox, operatorComboBox, valueTextBox, edgeComboBox);
        removeButton.Click += (_, _) =>
        {
            TriggerConditionsPanel.Children.Remove(rowGrid);
            _triggerConditionRows.Remove(row);
            ApplyTriggerToCurrentCapture();
        };
        signalComboBox.SelectionChanged += (_, _) => UpdateProbeFpgaStatus();
        typeComboBox.SelectionChanged += (_, _) => UpdateProbeFpgaStatus();
        operatorComboBox.SelectionChanged += (_, _) => UpdateProbeFpgaStatus();
        valueTextBox.TextChanged += (_, _) => UpdateProbeFpgaStatus();
        edgeComboBox.SelectionChanged += (_, _) => UpdateProbeFpgaStatus();

        _triggerConditionRows.Add(row);
        TriggerConditionsPanel.Children.Add(rowGrid);
    }

    private void ClearTriggerBuilder()
    {
        TriggerConditionsPanel.Children.Clear();
        _triggerConditionRows.Clear();
        _triggerExpression = new LogicTriggerExpression();
        _matchedTriggerSample = null;
        if (_currentCapture != null)
        {
            _currentCapture.TriggerSampleIndex = null;
            WaveformView.SetCapture(_currentCapture);
        }

        AddTriggerConditionRow();
        UpdateProbeFpgaStatus();
    }

    private void ApplyTriggerBuilderMode()
    {
        bool advanced = string.Equals(TriggerBuilderModeComboBox.SelectedItem as string, "Advanced", StringComparison.OrdinalIgnoreCase);
        TriggerConditionsPanel.IsEnabled = advanced;
        AddTriggerConditionButton.IsEnabled = advanced;
        TriggerCombineComboBox.IsEnabled = advanced;
        ApplyTriggerToCurrentCapture();
    }

    private void ApplyTriggerExpressionToUi(LogicTriggerExpression expression, int triggerPositionPercent)
    {
        TriggerPositionNumericUpDown.Value = Math.Clamp(triggerPositionPercent, 0, 100);
        TriggerBuilderModeComboBox.SelectedItem = expression.Mode == LogicTriggerBuilderMode.Advanced ? "Advanced" : "Basic";
        TriggerCombineComboBox.SelectedItem = expression.CombineMode == LogicTriggerCombineMode.Or ? "OR" : "AND";

        TriggerConditionsPanel.Children.Clear();
        _triggerConditionRows.Clear();
        if (expression.Conditions.Count == 0)
        {
            AddTriggerConditionRow();
            TriggerModeComboBox.SelectedItem = nameof(LogicTriggerMode.None);
            return;
        }

        foreach (LogicTriggerCondition condition in expression.Conditions)
        {
            AddTriggerConditionRow(condition);
        }

        if (expression.Mode == LogicTriggerBuilderMode.Basic)
        {
            LogicTriggerCondition condition = expression.Conditions[0];
            TriggerChannelComboBox.SelectedItem = condition.ChannelIndexes.FirstOrDefault();
            TriggerModeComboBox.SelectedItem = condition.ConditionType == LogicTriggerConditionType.Edge
                ? condition.EdgeType == LogicTriggerEdge.Falling ? "Falling" : "Rising"
                : LogicTriggerCondition.ParseInteger(condition.Value, 1) == 0 ? "Low" : "High";
        }

        ApplyTriggerBuilderMode();
    }

    private void ApplyTriggerToCurrentCapture()
    {
        _triggerExpression = BuildTriggerExpression();
        _matchedTriggerSample = _currentCapture == null ? null : _triggerExpression.FindMatch(_currentCapture);
        if (_currentCapture != null)
        {
            _currentCapture.TriggerSampleIndex = _matchedTriggerSample;
            WaveformView.SetCapture(_currentCapture);
            ProtocolAnalyzerPanel.SetCapture(_currentCapture);
        }

        UpdateProbeFpgaStatus();
    }

    private LogicTriggerExpression BuildTriggerExpression()
    {
        string modeText = TriggerBuilderModeComboBox.SelectedItem as string ?? "Basic";
        bool advanced = string.Equals(modeText, "Advanced", StringComparison.OrdinalIgnoreCase);

        if (!advanced)
        {
            string triggerMode = TriggerModeComboBox.SelectedItem as string ?? nameof(LogicTriggerMode.None);
            if (string.Equals(triggerMode, nameof(LogicTriggerMode.None), StringComparison.OrdinalIgnoreCase))
            {
                return new LogicTriggerExpression
                {
                    Mode = LogicTriggerBuilderMode.Basic
                };
            }

            int channel = TriggerChannelComboBox.SelectedItem is int selectedChannel ? selectedChannel : 0;
            LogicTriggerCondition condition = new()
            {
                SignalName = GetSignalLabel(channel),
                ChannelIndexes = [channel],
                ConditionType = triggerMode is "Rising" or "Falling"
                    ? LogicTriggerConditionType.Edge
                    : LogicTriggerConditionType.Level,
                EdgeType = string.Equals(triggerMode, "Falling", StringComparison.OrdinalIgnoreCase)
                    ? LogicTriggerEdge.Falling
                    : LogicTriggerEdge.Rising,
                Operator = LogicTriggerOperator.Equal,
                Value = string.Equals(triggerMode, "Low", StringComparison.OrdinalIgnoreCase) ? "0" : "1"
            };

            return new LogicTriggerExpression
            {
                Mode = LogicTriggerBuilderMode.Basic,
                CombineMode = LogicTriggerCombineMode.And,
                Conditions = [condition]
            };
        }

        return new LogicTriggerExpression
        {
            Mode = LogicTriggerBuilderMode.Advanced,
            CombineMode = string.Equals(TriggerCombineComboBox.SelectedItem as string, "OR", StringComparison.OrdinalIgnoreCase)
                ? LogicTriggerCombineMode.Or
                : LogicTriggerCombineMode.And,
            Conditions = _triggerConditionRows.Select(BuildConditionFromRow).Where(condition => condition != null).Cast<LogicTriggerCondition>().ToList()
        };
    }

    private LogicTriggerCondition? BuildConditionFromRow(TriggerConditionRowControls row)
    {
        string signalText = row.SignalComboBox.SelectedItem as string ?? "CH0";
        int[] channels = ResolveSignalChannels(signalText);
        if (channels.Length == 0)
        {
            return null;
        }

        string typeText = row.TypeComboBox.SelectedItem as string ?? "Level";
        LogicTriggerConditionType type = typeText switch
        {
            "Edge" => LogicTriggerConditionType.Edge,
            "BusCompare" => LogicTriggerConditionType.BusCompare,
            _ => channels.Length > 1 ? LogicTriggerConditionType.BusCompare : LogicTriggerConditionType.Level
        };

        return new LogicTriggerCondition
        {
            SignalName = signalText,
            ChannelIndexes = channels.ToList(),
            ConditionType = type,
            Operator = ParseTriggerOperator(row.OperatorComboBox.SelectedItem as string),
            Value = row.ValueTextBox.Text ?? "1",
            EdgeType = Enum.TryParse(row.EdgeComboBox.SelectedItem as string, ignoreCase: true, out LogicTriggerEdge edge)
                ? edge
                : LogicTriggerEdge.Rising
        };
    }

    private int[] ResolveSignalChannels(string signalText)
    {
        LogicBusDefinition? bus = _busDefinitions.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayName, signalText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, signalText, StringComparison.OrdinalIgnoreCase));
        if (bus != null)
        {
            return bus.Channels.ToArray();
        }

        LogicSignalDefinition? signal = _signalDefinitions.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayName, signalText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, signalText, StringComparison.OrdinalIgnoreCase));
        if (signal != null)
        {
            return [signal.Channel];
        }

        int chIndex = signalText.IndexOf("CH", StringComparison.OrdinalIgnoreCase);
        if (chIndex >= 0)
        {
            int start = chIndex + 2;
            int length = 0;
            while (start + length < signalText.Length && char.IsDigit(signalText[start + length]))
            {
                length++;
            }

            if (length > 0 && int.TryParse(signalText.Substring(start, length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
            {
                return [channel];
            }
        }

        return [];
    }

    private string GetSignalLabel(int channel)
    {
        return GetSignalLabels(GetChannelCount()).ElementAtOrDefault(channel) ?? $"CH{channel}";
    }

    private static LogicTriggerOperator ParseTriggerOperator(string? value)
    {
        return value switch
        {
            "!=" => LogicTriggerOperator.NotEqual,
            "<" => LogicTriggerOperator.LessThan,
            "<=" => LogicTriggerOperator.LessThanOrEqual,
            ">" => LogicTriggerOperator.GreaterThan,
            ">=" => LogicTriggerOperator.GreaterThanOrEqual,
            _ => LogicTriggerOperator.Equal
        };
    }

    private void UpdateQualificationState()
    {
        _qualificationMode = string.Equals(QualificationModeComboBox.SelectedItem as string, "Store When Condition True", StringComparison.OrdinalIgnoreCase)
            ? LogicQualificationMode.StoreWhenTrue
            : LogicQualificationMode.StoreAll;
        _qualificationConditionText = QualificationConditionTextBox.Text ?? "";
        UpdateProbeFpgaStatus();
    }

    private LogicCapture ApplyCaptureQualification(LogicCapture capture)
    {
        if (_qualificationMode != LogicQualificationMode.StoreWhenTrue)
        {
            _qualificationStoredSamples = capture.SampleCount;
            _qualificationSkippedSamples = 0;
            _qualificationTimingStatus = "Continuous";
            return capture;
        }

        LogicTriggerCondition? condition = ParseQualificationCondition();
        if (condition == null)
        {
            _qualificationStoredSamples = capture.SampleCount;
            _qualificationSkippedSamples = 0;
            _qualificationTimingStatus = "Invalid condition";
            return capture;
        }

        List<LogicSample> qualified = [];
        for (int sample = 0; sample < capture.SampleCount; sample++)
        {
            if (condition.Evaluate(capture, sample))
            {
                qualified.Add(new LogicSample(qualified.Count, capture.Samples[sample].Value));
            }
        }

        _qualificationStoredSamples = qualified.Count;
        _qualificationSkippedSamples = capture.SampleCount - qualified.Count;
        _qualificationTimingStatus = "Compressed samples";
        return qualified.Count == capture.SampleCount
            ? capture
            : new LogicCapture(capture.ChannelCount, capture.SampleRateHz, qualified);
    }

    private LogicTriggerCondition? ParseQualificationCondition()
    {
        string text = _qualificationConditionText.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        int[] channels = ResolveSignalChannels(parts[0]);
        if (channels.Length == 0)
        {
            return null;
        }

        if (parts.Length == 2 && Enum.TryParse(parts[1], ignoreCase: true, out LogicTriggerEdge edge))
        {
            return new LogicTriggerCondition
            {
                SignalName = parts[0],
                ChannelIndexes = channels.ToList(),
                ConditionType = LogicTriggerConditionType.Edge,
                EdgeType = edge
            };
        }

        if (parts.Length < 3)
        {
            return null;
        }

        return new LogicTriggerCondition
        {
            SignalName = parts[0],
            ChannelIndexes = channels.ToList(),
            ConditionType = channels.Length > 1 ? LogicTriggerConditionType.BusCompare : LogicTriggerConditionType.Level,
            Operator = ParseTriggerOperator(parts[1]),
            Value = parts[2]
        };
    }

    private void UpdateAutoCaptureState()
    {
        _autoCaptureLimit = ParseAutoCaptureLimit(AutoCaptureLimitComboBox.SelectedItem as string);
        UpdateProbeFpgaStatus();
    }

    private static int ParseAutoCaptureLimit(string? value)
    {
        if (string.Equals(value, "Infinite", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private void ToggleConnection()
    {
        if (_serialPort?.IsOpen == true)
        {
            Disconnect();
            return;
        }

        Connect();
    }

    private void Connect()
    {
        string? portName = PortComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(portName))
        {
            SetStatus("No port selected.");
            return;
        }

        int baudRate = BaudComboBox.SelectedItem is int selectedBaudRate ? selectedBaudRate : 115200;

        try
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                Encoding = Encoding.UTF8,
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true
            };
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.Open();
            _parser.Reset();
            _probeTextBuffer.Clear();
            _connectedAt = DateTime.Now;
            _lastSeenPort = portName;
            _probeConnectionState = "Unknown";
            _probeCaptureState = "Idle";
            _probeParserState = "Waiting for FPGA probe";
            _probeLastError = "No valid FPGA probe response.";
            SetConnectedUiState(true);
            SetStatus($"Connected to {portName} at {baudRate} baud.");
            QueryProbeInfo();
            _ = VerifyProbeHandshakeAsync(_connectedAt.Value);
        }
        catch (Exception ex)
        {
            _probeLastError = ex.Message;
            _probeParserState = "Connect failed";
            _probeConnectionState = "Disconnected";
            SetStatus($"Connect failed: {ex.Message}");
            _serialPort?.Dispose();
            _serialPort = null;
        }
    }

    private void Disconnect()
    {
        StopCaptureButton.IsEnabled = false;
        _isCapturing = false;

        if (_serialPort != null)
        {
            _serialPort.DataReceived -= SerialPort_DataReceived;
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }

            _serialPort.Dispose();
            _serialPort = null;
        }

        SetConnectedUiState(false);
        _connectedAt = null;
        _probeCaptureState = "Idle";
        _probeParserState = "Disconnected";
        _probeConnectionState = "Disconnected";
        _probeLastError = "No FPGA probe detected";
        SetStatus("Disconnected.");
    }

    private void QueryProbeInfo()
    {
        try
        {
            _serialPort?.Write("INFO\n");
        }
        catch (Exception ex)
        {
            _probeLastError = ex.Message;
            _probeParserState = "Handshake failed";
        }
    }

    private async System.Threading.Tasks.Task VerifyProbeHandshakeAsync(DateTime connectionStartedAt)
    {
        await System.Threading.Tasks.Task.Delay(1600);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_connectedAt != connectionStartedAt || !IsConnected || _probeId != "-")
            {
                return;
            }

            _probeConnectionState = "Unknown";
            _probeParserState = "Waiting for FPGA probe";
            _probeLastError = "No valid FPGA probe response.";
            UpdateProbeFpgaStatus();
        });
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        SerialPort? port = _serialPort;
        if (port == null)
        {
            return;
        }

        try
        {
            int count = port.BytesToRead;
            if (count <= 0)
            {
                return;
            }

            byte[] buffer = new byte[count];
            int read = port.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return;
            }

            DateTime packetAt = DateTime.Now;
            string incomingText = Encoding.UTF8.GetString(buffer, 0, read);
            IReadOnlyList<LogicCapture> captures = _parser.Append(buffer.AsSpan(0, read));
            Dispatcher.UIThread.Post(() =>
            {
                _lastPacketAt = packetAt;
                _lastPacketBytes = read;
                _probeRxBytes += read;
                ProcessProbeText(incomingText);
                _probeParserState = captures.Count > 0
                    ? "Capture packet decoded"
                    : _probeConnectionState == "Connected" ? "Waiting for LGCM packet" : _probeParserState;
                UpdateProbeFpgaStatus();

                foreach (LogicCapture capture in captures)
                {
                    LoadCapture(capture);
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _probeLastError = ex.Message;
                _probeParserState = "Read failed";
                _probeConnectionState = IsConnected ? "Unknown" : "Disconnected";
                SetStatus($"Serial read failed: {ex.Message}");
            });
        }
    }

    private void ProcessProbeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _probeTextBuffer.Append(text);
        string bufferText = _probeTextBuffer.ToString();
        int newlineIndex;
        while ((newlineIndex = bufferText.IndexOf('\n', StringComparison.Ordinal)) >= 0)
        {
            string line = bufferText[..newlineIndex].Trim('\r', '\n', ' ');
            bufferText = bufferText[(newlineIndex + 1)..];
            ParseProbeLine(line);
        }

        _probeTextBuffer.Clear();
        if (bufferText.Length > 2048)
        {
            bufferText = bufferText[^2048..];
        }

        _probeTextBuffer.Append(bufferText);
    }

    private void ParseProbeLine(string line)
    {
        if (line.Length == 0 || !line.StartsWith("LOGICOM_PROBE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dictionary<string, string> values = line
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        _probeId = values.GetValueOrDefault("id", _probeId);
        _firmwareVersion = values.GetValueOrDefault("fw", _firmwareVersion);
        _bitstreamVersion = values.GetValueOrDefault("bitstream", _bitstreamVersion);
        _probeChannelCount = values.GetValueOrDefault("channels", _probeChannelCount);
        _maxSampleRate = FormatHertz(values.GetValueOrDefault("max_rate", _maxSampleRate));
        _captureDepth = values.GetValueOrDefault("depth", _captureDepth);
        _fpgaClock = FormatHertz(values.GetValueOrDefault("clock", _fpgaClock));
        _inputVoltage = values.GetValueOrDefault("vin", values.GetValueOrDefault("voltage", _inputVoltage));
        _probeConnectionState = "Connected";
        _probeParserState = "Probe identified";
        _probeLastError = "None";
    }

    private void SendCaptureCommand(string command)
    {
        if (_serialPort?.IsOpen != true)
        {
            SetStatus("Not connected. Use Generate Mock Capture without hardware.");
            return;
        }

        string triggerMode = TriggerModeComboBox.SelectedItem as string ?? nameof(LogicTriggerMode.None);
        int triggerChannel = TriggerChannelComboBox.SelectedItem is int selectedTriggerChannel ? selectedTriggerChannel : 0;
        _triggerExpression = BuildTriggerExpression();
        CapturePlan capturePlan = GetCapturePlan();
        (int preTriggerSamples, int postTriggerSamples) = GetTriggerSampleSplit(capturePlan.ActualSamples);
        string triggerExpression = _triggerExpression.ToExpressionText().Replace(" ", "_", StringComparison.Ordinal);
        string protocolCommand = string.Format(
            CultureInfo.InvariantCulture,
            "{0} RATE={1} SAMPLES={2} RECORD_TIME_US={3} CHANNELS={4} TRIGGER_CH={5} TRIGGER={6} TRIGGER_POS={7} PRE={8} POST={9} EXPR={10}\n",
            command,
            capturePlan.SampleRateHz,
            capturePlan.ActualSamples,
            (long)Math.Round(capturePlan.ActualDurationSeconds * 1_000_000),
            GetChannelCount(),
            triggerChannel,
            triggerMode.ToUpperInvariant(),
            GetTriggerPositionPercent(),
            preTriggerSamples,
            postTriggerSamples,
            triggerExpression);

        try
        {
            _parser.Reset();
            _serialPort.Write(protocolCommand);
            _isCapturing = command == "START";
            _probeCaptureState = command == "START" ? "Capturing" : "Armed";
            _autoCaptureState = _isAutoCaptureRunning ? "Waiting for trigger" : _autoCaptureState;
            _probeParserState = "Waiting for LGCM packet";
            _probeLastError = "None";
            StopCaptureButton.IsEnabled = _isCapturing;
            SetStatus(capturePlan.IsClamped
                ? $"{command} command sent with {capturePlan.ActualSamples:N0} samples. Clamped to probe depth."
                : $"{command} command sent with {capturePlan.ActualSamples:N0} samples. Waiting for LGCM capture packet.");
        }
        catch (Exception ex)
        {
            _probeLastError = ex.Message;
            _probeCaptureState = "Command failed";
            SetStatus($"Command failed: {ex.Message}");
        }
    }

    private void SendStopCommand()
    {
        if (_serialPort?.IsOpen != true)
        {
            SetStatus("Not connected.");
            return;
        }

        try
        {
            _serialPort.Write("STOP\n");
            _isCapturing = false;
            _probeCaptureState = "Stopped";
            _probeParserState = "Stopped";
            StopCaptureButton.IsEnabled = false;
            SetStatus("STOP command sent.");
        }
        catch (Exception ex)
        {
            _probeLastError = ex.Message;
            SetStatus($"Stop failed: {ex.Message}");
        }
    }

    private void ClearCapture()
    {
        _currentCapture = null;
        _parser.Reset();
        WaveformView.SetCapture(null);
        ProtocolAnalyzerPanel.SetCapture(null);
        CursorTextBlock.Text = "Cursor: -";
        _lastCaptureAt = null;
        _probeCaptureState = "Idle";
        _probeParserState = IsConnected ? "Connected" : "Idle";
        _matchedTriggerSample = null;
        _captureFileName = "-";
        _captureFileSaved = true;
        _qualificationStoredSamples = 0;
        _qualificationSkippedSamples = 0;
        _qualificationTimingStatus = "Continuous";
        SetStatus("Capture cleared.");
        UpdateCaptureHistoryButtons();
    }

    private void LoadCapture(LogicCapture capture, bool addToHistory = true)
    {
        capture = ApplyCaptureQualification(capture);
        _triggerExpression = BuildTriggerExpression();
        _matchedTriggerSample = _triggerExpression.FindMatch(capture);
        capture.TriggerSampleIndex = _matchedTriggerSample;
        _currentCapture = capture;
        _isCapturing = false;
        _lastCaptureAt = DateTime.Now;
        _probeCaptureState = "Complete";
        _probeParserState = "Ready";
        StopCaptureButton.IsEnabled = false;
        _captureFileSaved = false;

        if (addToHistory)
        {
            _captureHistory.Add(capture);
            _captureHistoryIndex = _captureHistory.Count - 1;
            if (_isAutoCaptureRunning)
            {
                _autoCapturesCompleted++;
            }
        }

        WaveformView.SetCapture(capture);
        WaveformView.SetSignalLabels(GetSignalLabels(capture.ChannelCount));
        ProtocolAnalyzerPanel.SetSignalLabels(GetSignalLabels(capture.ChannelCount));
        ProtocolAnalyzerPanel.SetCapture(capture);
        SetStatus($"Loaded {capture.SampleCount} samples, {capture.ChannelCount} channels, {capture.SampleRateHz} Hz.");
        UpdateCaptureHistoryButtons();
        ContinueAutoCaptureIfNeeded();
    }

    private LogicCapture CreateMockCapture()
    {
        string mockType = MockTypeComboBox.SelectedItem as string ?? "General";
        int sampleRate = GetSampleRate();
        int protocolSampleRate = Math.Max(sampleRate, 1_000_000);
        return mockType switch
        {
            "UART" => LogicMockCaptureGenerator.GenerateUart(protocolSampleRate),
            "I2C" => LogicMockCaptureGenerator.GenerateI2c(protocolSampleRate),
            "SPI" => LogicMockCaptureGenerator.GenerateSpi(protocolSampleRate),
            "CAN" => LogicMockCaptureGenerator.GenerateCan(protocolSampleRate),
            _ => LogicMockCaptureGenerator.Generate(sampleRateHz: sampleRate)
        };
    }

    private void StartAutoCapture()
    {
        _isAutoCaptureRunning = true;
        _autoCapturesCompleted = 0;
        _autoCaptureLimit = ParseAutoCaptureLimit(AutoCaptureLimitComboBox.SelectedItem as string);
        if (!IsConnected && _autoCaptureLimit == 0)
        {
            _autoCaptureLimit = 5;
        }
        _autoCaptureState = "Waiting for trigger";
        StartAutoCaptureButton.IsEnabled = false;
        StopAutoCaptureButton.IsEnabled = true;

        if (IsConnected)
        {
            SendCaptureCommand("START");
        }
        else
        {
            LoadCapture(CreateMockCapture());
        }

        UpdateProbeFpgaStatus();
    }

    private void StopAutoCapture()
    {
        _isAutoCaptureRunning = false;
        _autoCaptureState = "Stopped";
        StartAutoCaptureButton.IsEnabled = true;
        StopAutoCaptureButton.IsEnabled = false;
        if (IsConnected && _isCapturing)
        {
            SendStopCommand();
        }
        else
        {
            UpdateProbeFpgaStatus();
        }
    }

    private void ContinueAutoCaptureIfNeeded()
    {
        if (!_isAutoCaptureRunning)
        {
            return;
        }

        bool reachedLimit = _autoCaptureLimit > 0 && _autoCapturesCompleted >= _autoCaptureLimit;
        if (reachedLimit)
        {
            _isAutoCaptureRunning = false;
            _autoCaptureState = "Complete";
            StartAutoCaptureButton.IsEnabled = true;
            StopAutoCaptureButton.IsEnabled = false;
            UpdateProbeFpgaStatus();
            return;
        }

        _autoCaptureState = "Re-arming";
        if (IsConnected)
        {
            Dispatcher.UIThread.Post(() => SendCaptureCommand("START"));
        }
        else
        {
            _ = ContinueMockAutoCaptureAsync();
        }
    }

    private async System.Threading.Tasks.Task ContinueMockAutoCaptureAsync()
    {
        await System.Threading.Tasks.Task.Delay(250);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isAutoCaptureRunning)
            {
                LoadCapture(CreateMockCapture());
            }
        });
    }

    private void ShowCaptureAt(int index)
    {
        if (index < 0 || index >= _captureHistory.Count)
        {
            return;
        }

        _captureHistoryIndex = index;
        LoadCapture(_captureHistory[index], addToHistory: false);
    }

    private void UpdateCaptureHistoryButtons()
    {
        int count = _captureHistory.Count;
        CaptureHistoryTextBlock.Text = count == 0 || _captureHistoryIndex < 0
            ? "Capture 0 of 0"
            : $"Capture {_captureHistoryIndex + 1} of {count}";
        PreviousCaptureButton.IsEnabled = _captureHistoryIndex > 0;
        NextCaptureButton.IsEnabled = _captureHistoryIndex >= 0 && _captureHistoryIndex < count - 1;
    }

    private void SetCursorFromCurrentSample(bool isCursorA)
    {
        int? sample = WaveformView.CurrentSampleIndex;
        if (!sample.HasValue && _currentCapture != null)
        {
            sample = _currentCapture.TriggerSampleIndex ?? 0;
        }

        if (!sample.HasValue)
        {
            return;
        }

        if (isCursorA)
        {
            WaveformView.SetCursorA(sample.Value);
        }
        else
        {
            WaveformView.SetCursorB(sample.Value);
        }
    }

    private void ClearCursors()
    {
        WaveformView.ClearCursors();
        _cursorASample = null;
        _cursorBSample = null;
        UpdateProbeFpgaStatus();
    }

    public async System.Threading.Tasks.Task SaveCaptureProjectAsync(bool saveAs = true)
    {
        LogicCapture? capture = _currentCapture;
        if (capture == null)
        {
            SetStatus("No capture to save.");
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        FilePickerSaveOptions options = new()
        {
            Title = "Save Logicom Capture",
            SuggestedFileName = $"logic-capture-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.logicapture",
            DefaultExtension = "logicapture",
            FileTypeChoices =
            [
                new FilePickerFileType("Logicom capture files")
                {
                    Patterns = ["*.logicapture"]
                }
            ]
        };

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        if (file == null)
        {
            return;
        }

        _triggerExpression = BuildTriggerExpression();
        LogicCaptureProject project = CreateCaptureProject(capture);
        string json = JsonSerializer.Serialize(project, CaptureJsonOptions);
        await using Stream stream = await file.OpenWriteAsync();
        using StreamWriter writer = new(stream);
        await writer.WriteAsync(json);
        _captureFileName = file.Name;
        _captureFileSaved = true;
        _lastSavedAt = DateTime.Now;
        SetStatus($"Saved {file.Name}.");
    }

    public async System.Threading.Tasks.Task OpenCaptureProjectAsync()
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open Logicom Capture",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Logicom capture files")
                    {
                        Patterns = ["*.logicapture", "*.logicomproj"]
                    }
                ]
            });

        IStorageFile? file = files.FirstOrDefault();
        if (file == null)
        {
            return;
        }

        await using Stream stream = await file.OpenReadAsync();
        using StreamReader reader = new(stream);
        string json = await reader.ReadToEndAsync();
        LogicCaptureProject? project = JsonSerializer.Deserialize<LogicCaptureProject>(json, CaptureJsonOptions);
        if (project == null || !string.Equals(project.Format, "LogicomCapture", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Invalid Logicom capture file.");
            return;
        }

        LogicCapture capture = CreateCaptureFromProject(project);
        _signalDefinitions.Clear();
        _signalDefinitions.AddRange(project.Signals ?? []);
        ConfigureSignalDefinitions(capture.ChannelCount);
        _busDefinitions.Clear();
        _busDefinitions.AddRange(project.Buses ?? []);
        _triggerExpression = project.Trigger ?? new LogicTriggerExpression();
        ApplyTriggerExpressionToUi(_triggerExpression, project.TriggerPositionPercent);
        QualificationModeComboBox.SelectedItem = string.Equals(project.QualificationMode, nameof(LogicQualificationMode.StoreWhenTrue), StringComparison.OrdinalIgnoreCase)
            ? "Store When Condition True"
            : "Store All Samples";
        QualificationConditionTextBox.Text = project.QualificationCondition;
        _cursorASample = project.Cursors?.A;
        _cursorBSample = project.Cursors?.B;
        if (_cursorASample.HasValue)
        {
            WaveformView.SetCursorA(_cursorASample.Value);
        }

        if (_cursorBSample.HasValue)
        {
            WaveformView.SetCursorB(_cursorBSample.Value);
        }

        LoadCapture(capture);
        _captureFileName = file.Name;
        _captureFileSaved = true;
        _lastSavedAt = DateTime.Now;
        SetStatus($"Opened {file.Name}.");
    }

    private LogicCaptureProject CreateCaptureProject(LogicCapture capture)
    {
        CapturePlan plan = GetCapturePlan();
        return new LogicCaptureProject
        {
            Format = "LogicomCapture",
            Version = 1,
            AppVersion = "0.3.1",
            CreatedAt = DateTime.UtcNow,
            SampleRateHz = capture.SampleRateHz,
            Samples = capture.SampleCount,
            Channels = capture.ChannelCount,
            RecordTimeSeconds = plan.ActualDurationSeconds,
            Trigger = _triggerExpression,
            TriggerPositionPercent = GetTriggerPositionPercent(),
            QualificationMode = _qualificationMode.ToString(),
            QualificationCondition = _qualificationConditionText,
            AutoRetriggerEnabled = _isAutoCaptureRunning,
            AutoCaptureLimit = AutoCaptureLimitComboBox.SelectedItem as string ?? "Infinite",
            Signals = _signalDefinitions.ToList(),
            Buses = _busDefinitions.ToList(),
            ProtocolDecoder = ProtocolAnalyzerPanel.SelectedProtocol,
            CursorSnapEnabled = _settings.DefaultCursorSnapMode,
            Cursors = new LogicCaptureCursors
            {
                A = _cursorASample,
                B = _cursorBSample
            },
            TriggerSampleIndex = capture.TriggerSampleIndex,
            ProbeId = _probeId,
            FirmwareVersion = _firmwareVersion,
            BitstreamVersion = _bitstreamVersion,
            SampleData = PackSampleData(capture)
        };
    }

    private static LogicCapture CreateCaptureFromProject(LogicCaptureProject project)
    {
        byte[] bytes = Convert.FromBase64String(project.SampleData ?? "");
        int bytesPerSample = project.Channels <= 8 ? 1 : 2;
        int sampleCount = Math.Min(project.Samples, bytes.Length / bytesPerSample);
        LogicSample[] samples = new LogicSample[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            int offset = index * bytesPerSample;
            uint value = bytesPerSample == 1
                ? bytes[offset]
                : (uint)(bytes[offset] | (bytes[offset + 1] << 8));
            samples[index] = new LogicSample(index, value);
        }

        LogicCapture capture = new(project.Channels == 16 ? 16 : 8, Math.Max(1, project.SampleRateHz), samples)
        {
            TriggerSampleIndex = project.TriggerSampleIndex
        };
        return capture;
    }

    private static string PackSampleData(LogicCapture capture)
    {
        int bytesPerSample = capture.ChannelCount <= 8 ? 1 : 2;
        byte[] bytes = new byte[capture.SampleCount * bytesPerSample];
        for (int index = 0; index < capture.SampleCount; index++)
        {
            uint value = capture.Samples[index].Value;
            int offset = index * bytesPerSample;
            bytes[offset] = (byte)(value & 0xff);
            if (bytesPerSample == 2)
            {
                bytes[offset + 1] = (byte)((value >> 8) & 0xff);
            }
        }

        return Convert.ToBase64String(bytes);
    }

    public async System.Threading.Tasks.Task ExportCaptureAsync(string format)
    {
        LogicCapture? capture = _currentCapture;
        if (capture == null)
        {
            SetStatus("No capture to export.");
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        string extension = format == "vcd" ? "vcd" : "csv";
        FilePickerSaveOptions options = new()
        {
            Title = $"Export Logic Capture {extension.ToUpperInvariant()}",
            SuggestedFileName = $"logic-capture-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{extension}",
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(extension.ToUpperInvariant() + " files")
                {
                    Patterns = ["*." + extension]
                }
            ]
        };

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        if (file == null)
        {
            return;
        }

        string text = format == "vcd"
            ? LogicCaptureExporter.ToVcd(capture)
            : LogicCaptureExporter.ToCsv(capture);

        await using Stream stream = await file.OpenWriteAsync();
        using StreamWriter writer = new(stream);
        await writer.WriteAsync(text);
        SetStatus($"Exported {file.Name}.");
    }

    public async System.Threading.Tasks.Task ExportDecodedCsvAsync()
    {
        await ProtocolAnalyzerPanel.ExportDecodedCsvAsync();
        UpdateProtocolStatus();
    }

    private int GetSampleRate()
    {
        return SampleRateNumericUpDown.Value.HasValue
            ? Math.Max(1, decimal.ToInt32(SampleRateNumericUpDown.Value.Value))
            : 1_000_000;
    }

    private CapturePlan GetCapturePlan()
    {
        int sampleRateHz = GetSampleRate();
        double requestedSeconds = Math.Max(double.Epsilon, GetRecordTimeSeconds());
        long requestedSamplesLong = Math.Max(1, (long)Math.Ceiling(sampleRateHz * requestedSeconds));
        int captureDepth = GetCaptureDepthSamples();
        int actualSamples = (int)Math.Min(requestedSamplesLong, captureDepth);
        double actualDurationSeconds = actualSamples / (double)sampleRateHz;
        bool isClamped = requestedSamplesLong > captureDepth;
        _capturePlanStatus = isClamped ? "Clamped to probe depth" : "Ready";

        return new CapturePlan(
            sampleRateHz,
            requestedSeconds,
            requestedSamplesLong,
            captureDepth,
            actualSamples,
            actualDurationSeconds,
            isClamped);
    }

    private int GetTriggerPositionPercent()
    {
        return TriggerPositionNumericUpDown.Value.HasValue
            ? Math.Clamp(decimal.ToInt32(TriggerPositionNumericUpDown.Value.Value), 0, 100)
            : 50;
    }

    private (int PreTriggerSamples, int PostTriggerSamples) GetTriggerSampleSplit(int sampleCount)
    {
        int pre = (int)Math.Round(sampleCount * (GetTriggerPositionPercent() / 100.0));
        pre = Math.Clamp(pre, 0, sampleCount);
        return (pre, sampleCount - pre);
    }

    private double GetRecordTimeSeconds()
    {
        decimal value = RecordTimeNumericUpDown.Value ?? 10;
        return decimal.ToDouble(value) * GetUnitSeconds(_recordTimeUnit);
    }

    private int GetCaptureDepthSamples()
    {
        string depthText = _captureDepth == "-" ? "" : _captureDepth;
        string digits = new(depthText.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : Math.Max(1, _settings.CaptureDepth);
    }

    private void HandleRecordTimeUnitChanged()
    {
        if (_syncingRecordTimeUnit)
        {
            return;
        }

        string newUnit = RecordTimeUnitComboBox.SelectedItem as string ?? "ms";
        double seconds = GetRecordTimeSeconds();
        _recordTimeUnit = newUnit;

        _syncingRecordTimeUnit = true;
        RecordTimeNumericUpDown.Value = (decimal)Math.Max(0.001, seconds / GetUnitSeconds(newUnit));
        _syncingRecordTimeUnit = false;
        UpdateProbeFpgaStatus();
    }

    private static double GetUnitSeconds(string unit)
    {
        return unit switch
        {
            "µs" => 0.000001,
            "s" => 1,
            _ => 0.001
        };
    }

    private int GetChannelCount()
    {
        return ChannelCountComboBox.SelectedItem is int selected ? selected : 8;
    }

    private void SetConnectedUiState(bool isConnected)
    {
        ConnectButton.Content = isConnected ? "Disconnect" : "Connect";
        PortComboBox.IsEnabled = !isConnected;
        BaudComboBox.IsEnabled = !isConnected;
        RefreshPortsButton.IsEnabled = !isConnected;
        StopCaptureButton.IsEnabled = isConnected && _isCapturing;
        ConnectionSettingsChanged?.Invoke();
        UpdateProbeFpgaStatus();
    }

    private void SetStatus(string text)
    {
        StatusTextBlock.Text = text;
        UpdateProbeFpgaStatus();
    }

    public void ShowProbeStatusPanel()
    {
        _showProbeStatus = true;
        ApplyPanelVisibility();
    }

    public void ShowProtocolAnalyzer()
    {
        _showProtocolAnalyzer = true;
        ApplyPanelVisibility();
    }

    private void HideProbeStatusPanel()
    {
        _showProbeStatus = false;
        ApplyPanelVisibility();
    }

    private void HideProtocolAnalyzer()
    {
        _showProtocolAnalyzer = false;
        ApplyPanelVisibility();
    }

    private void ConfigurePanelContextMenus()
    {
        MenuItem showProbeFromRootItem = new()
        {
            Header = "Show FPGA Probe Status"
        };
        MenuItem hideProbeFromRootItem = new()
        {
            Header = "Hide FPGA Probe Status"
        };
        MenuItem showProtocolFromRootItem = new()
        {
            Header = "Show Protocol Analyzer"
        };
        MenuItem hideProtocolFromRootItem = new()
        {
            Header = "Hide Protocol Analyzer"
        };
        showProbeFromRootItem.Click += (_, _) => ShowProbeStatusPanel();
        hideProbeFromRootItem.Click += (_, _) => HideProbeStatusPanel();
        showProtocolFromRootItem.Click += (_, _) => ShowProtocolAnalyzer();
        hideProtocolFromRootItem.Click += (_, _) => HideProtocolAnalyzer();
        AnalyzerContentGrid.ContextMenu = new ContextMenu
        {
            Items =
            {
                showProbeFromRootItem,
                hideProbeFromRootItem,
                showProtocolFromRootItem,
                hideProtocolFromRootItem
            }
        };

        MenuItem hideProbeStatusItem = new()
        {
            Header = "Hide FPGA Probe Status"
        };
        hideProbeStatusItem.Click += (_, _) => HideProbeStatusPanel();
        ProbeStatusPanel.ContextMenu = new ContextMenu
        {
            Items =
            {
                hideProbeStatusItem
            }
        };

        MenuItem hideProtocolItem = new()
        {
            Header = "Hide Protocol Analyzer"
        };
        hideProtocolItem.Click += (_, _) => HideProtocolAnalyzer();
        ProtocolAnalyzerPanel.ContextMenu = new ContextMenu
        {
            Items =
            {
                hideProtocolItem
            }
        };

        MenuItem renameSignalItem = new()
        {
            Header = "Rename Signal"
        };
        MenuItem createBusItem = new()
        {
            Header = "Create Bus From Selected Channels"
        };
        MenuItem setCursorAItem = new()
        {
            Header = "Set Cursor A"
        };
        MenuItem setCursorBItem = new()
        {
            Header = "Set Cursor B"
        };
        MenuItem clearCursorsItem = new()
        {
            Header = "Clear Cursors"
        };
        MenuItem radixHexItem = new()
        {
            Header = "Change Radix: Hex"
        };
        MenuItem radixBinaryItem = new()
        {
            Header = "Change Radix: Binary"
        };
        MenuItem addAnnotationItem = new()
        {
            Header = "Add Annotation"
        };
        MenuItem hideSignalItem = new()
        {
            Header = "Hide Signal",
            IsEnabled = false
        };
        MenuItem moveSignalUpItem = new()
        {
            Header = "Move Signal Up",
            IsEnabled = false
        };
        MenuItem moveSignalDownItem = new()
        {
            Header = "Move Signal Down",
            IsEnabled = false
        };
        renameSignalItem.Click += async (_, _) => await RenameCurrentSignalAsync();
        createBusItem.Click += (_, _) => CreateBusFromSelectedChannels();
        setCursorAItem.Click += (_, _) => SetCursorFromCurrentSample(isCursorA: true);
        setCursorBItem.Click += (_, _) => SetCursorFromCurrentSample(isCursorA: false);
        clearCursorsItem.Click += (_, _) => ClearCursors();
        radixHexItem.Click += (_, _) => SetDefaultBusRadix("Hex");
        radixBinaryItem.Click += (_, _) => SetDefaultBusRadix("Binary");
        WaveformView.ContextMenu = new ContextMenu
        {
            Items =
            {
                renameSignalItem,
                createBusItem,
                setCursorAItem,
                setCursorBItem,
                clearCursorsItem,
                addAnnotationItem,
                radixHexItem,
                radixBinaryItem,
                hideSignalItem,
                moveSignalUpItem,
                moveSignalDownItem
            }
        };
    }

    private void ApplyPanelVisibility()
    {
        if (!IsInitialized)
        {
            return;
        }

        ProbeStatusPanel.IsVisible = _showProbeStatus;

        if (_showProtocolAnalyzer)
        {
            ProtocolAnalyzerPanel.IsVisible = true;
            ProtocolGridSplitter.IsVisible = true;
            AnalyzerContentGrid.RowDefinitions[1].Height = new GridLength(6);
            AnalyzerContentGrid.RowDefinitions[2].Height = _savedProtocolRowHeight.Value > 0
                ? _savedProtocolRowHeight
                : new GridLength(220);
        }
        else
        {
            if (AnalyzerContentGrid.RowDefinitions[2].Height.Value > 0)
            {
                _savedProtocolRowHeight = AnalyzerContentGrid.RowDefinitions[2].Height;
            }

            ProtocolAnalyzerPanel.IsVisible = false;
            ProtocolGridSplitter.IsVisible = false;
            AnalyzerContentGrid.RowDefinitions[1].Height = new GridLength(0);
            AnalyzerContentGrid.RowDefinitions[2].Height = new GridLength(0);
        }

        if (AnalyzerRootGrid.ColumnDefinitions.Count > 1)
        {
            AnalyzerRootGrid.ColumnDefinitions[1].Width = _showProbeStatus ? new GridLength(340) : new GridLength(0);
        }

        AnalyzerRootGrid.ColumnSpacing = _showProbeStatus ? 12 : 0;
    }

    private async System.Threading.Tasks.Task RenameCurrentSignalAsync()
    {
        int? channel = WaveformView.CurrentChannelIndex;
        if (!channel.HasValue)
        {
            SetStatus("Hover a channel label or trace before renaming.");
            return;
        }

        LogicSignalDefinition? signal = _signalDefinitions.FirstOrDefault(item => item.Channel == channel.Value);
        if (signal == null)
        {
            return;
        }

        Window? owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            return;
        }

        TextBox nameTextBox = new()
        {
            Text = signal.Name,
            Margin = new Avalonia.Thickness(0, 0, 0, 12)
        };
        Button cancelButton = new()
        {
            Content = "Cancel",
            Width = 90
        };
        Button saveButton = new()
        {
            Content = "Save",
            Width = 90
        };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                cancelButton,
                saveButton
            }
        };
        Window dialog = new()
        {
            Title = $"Rename CH{channel.Value}",
            Width = 360,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"Signal name for CH{channel.Value}" },
                    nameTextBox,
                    buttons
                }
            }
        };
        cancelButton.Click += (_, _) => dialog.Close(null);
        saveButton.Click += (_, _) => dialog.Close(nameTextBox.Text);

        string? result = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        signal.Name = result.Trim();
        WaveformView.SetSignalLabels(GetSignalLabels(GetChannelCount()));
        ProtocolAnalyzerPanel.SetSignalLabels(GetSignalLabels(GetChannelCount()));
        RefreshTriggerConditionRows();
        _selectedSignal = signal.DisplayName;
        _captureFileSaved = false;
        UpdateProbeFpgaStatus();
    }

    private void CreateBusFromSelectedChannels()
    {
        int startChannel = WaveformView.CurrentChannelIndex ?? 0;
        int channelCount = GetChannelCount();
        List<int> channels = Enumerable.Range(startChannel, Math.Min(4, channelCount - startChannel)).ToList();
        if (channels.Count == 0)
        {
            channels = Enumerable.Range(0, Math.Min(4, channelCount)).ToList();
        }

        LogicBusDefinition bus = new()
        {
            Name = $"BUS{_busDefinitions.Count}",
            Channels = channels,
            LsbChannel = channels[0],
            Radix = _settings.DefaultBusRadix
        };
        _busDefinitions.Add(bus);
        _selectedSignal = bus.DisplayName;
        WaveformView.SetBusDefinitions(_busDefinitions);
        RefreshTriggerConditionRows();
        _captureFileSaved = false;
        UpdateProbeFpgaStatus();
    }

    private void SetDefaultBusRadix(string radix)
    {
        if (_busDefinitions.Count == 0)
        {
            return;
        }

        _busDefinitions[^1].Radix = radix;
        WaveformView.SetBusDefinitions(_busDefinitions);
        _captureFileSaved = false;
        UpdateProbeFpgaStatus();
    }

    private void UpdateProtocolStatus()
    {
        _decodeStatus = ProtocolAnalyzerPanel.DecodeStatus;
        _decodedFrames = ProtocolAnalyzerPanel.FrameCount;
        _decodeErrors = ProtocolAnalyzerPanel.ErrorCount;
        _lastDecodeAt = ProtocolAnalyzerPanel.LastDecodeAt;
        UpdateProbeFpgaStatus();
    }

    private void UpdateProbeFpgaStatus()
    {
        if (!IsInitialized)
        {
            return;
        }

        string selectedPort = SelectedPort ?? "-";
        string lastCapture = _currentCapture == null
            ? "-"
            : $"{_currentCapture.SampleCount} samples ({FormatRelativeTime(_lastCaptureAt)})";
        CapturePlan capturePlan = GetCapturePlan();
        string sampleRate = capturePlan.SampleRateHz.ToString("N0", CultureInfo.InvariantCulture);
        string visibleSamples = _currentCapture == null ? "-" : _currentCapture.SampleCount.ToString("N0", CultureInfo.InvariantCulture);
        string totalSamples = _currentCapture == null ? "-" : _currentCapture.SampleCount.ToString("N0", CultureInfo.InvariantCulture);
        LogicTriggerExpression triggerExpression = BuildTriggerExpression();
        (int preTriggerSamples, int postTriggerSamples) = GetTriggerSampleSplit(capturePlan.ActualSamples);
        string triggerMode = triggerExpression.Mode.ToString();
        string triggerChannel = TriggerChannelComboBox.SelectedItem is int channel ? $"CH{channel}" : "CH0";
        string triggerExpressionText = triggerExpression.ToExpressionText();
        string probeId = IsConnected ? _probeId : "-";
        string firmware = IsConnected ? _firmwareVersion : "-";
        string bitstream = IsConnected ? _bitstreamVersion : "-";
        string fpgaClock = IsConnected ? _fpgaClock : "-";
        string inputVoltage = IsConnected ? _inputVoltage : "-";
        string probeChannels = IsConnected ? _probeChannelCount : "-";
        string maxSampleRate = IsConnected ? _maxSampleRate : "-";
        string captureDepth = IsConnected ? _captureDepth : "-";

        StringBuilder builder = new();
        AppendStatusSection(builder, "PROBE FPGA");
        AppendProbeStatusRow(builder, "Connection", IsConnected ? _probeConnectionState : "Disconnected");
        AppendProbeStatusRow(builder, "Selected Port", selectedPort);
        AppendProbeStatusRow(builder, "Last Seen Port", _lastSeenPort ?? "-");
        AppendProbeStatusRow(builder, "Probe ID", probeId);
        AppendProbeStatusRow(builder, "Firmware", firmware);
        AppendProbeStatusRow(builder, "Bitstream", bitstream);
        AppendProbeStatusRow(builder, "FPGA Clock", fpgaClock);
        AppendProbeStatusRow(builder, "Input Voltage", inputVoltage);
        AppendProbeStatusRow(builder, "Channels", probeChannels);
        AppendProbeStatusRow(builder, "Max Sample Rate", maxSampleRate);
        AppendProbeStatusRow(builder, "Capture Depth", captureDepth);
        if (!IsConnected)
        {
            AppendProbeStatusRow(builder, "Status", "Waiting for FPGA probe");
            AppendProbeStatusRow(builder, "Last Error", "No FPGA probe detected");
        }
        builder.AppendLine();

        AppendStatusSection(builder, "CAPTURE");
        AppendProbeStatusRow(builder, "State", _probeCaptureState);
        AppendProbeStatusRow(builder, "Sample Rate", sampleRate + " Hz");
        AppendProbeStatusRow(builder, "Record Time", FormatRequestedRecordTime());
        AppendProbeStatusRow(builder, "Requested Samples", capturePlan.RequestedSamples.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Actual Samples", capturePlan.ActualSamples.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Actual Duration", FormatDurationSeconds(capturePlan.ActualDurationSeconds));
        AppendProbeStatusRow(builder, "Capture Depth", capturePlan.CaptureDepth.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Visible Samples", visibleSamples);
        AppendProbeStatusRow(builder, "Total Samples", totalSamples);
        AppendProbeStatusRow(builder, "Trigger Mode", triggerMode);
        AppendProbeStatusRow(builder, "Trigger Channel", triggerChannel);
        AppendProbeStatusRow(builder, "Trigger Position", GetTriggerPositionPercent().ToString(CultureInfo.InvariantCulture) + "%");
        AppendProbeStatusRow(builder, "Pre-trigger", preTriggerSamples.ToString("N0", CultureInfo.InvariantCulture) + " samples");
        AppendProbeStatusRow(builder, "Post-trigger", postTriggerSamples.ToString("N0", CultureInfo.InvariantCulture) + " samples");
        AppendProbeStatusRow(builder, "Trigger Sample", _matchedTriggerSample?.ToString("N0", CultureInfo.InvariantCulture) ?? "-");
        AppendProbeStatusRow(builder, "Last Capture", lastCapture);
        AppendProbeStatusRow(builder, "Transfer Bytes", FormatByteSize(_probeRxBytes));
        AppendProbeStatusRow(builder, "Packet Loss", "0");
        AppendProbeStatusRow(builder, "CRC Errors", _crcErrors.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Status", _capturePlanStatus);
        builder.AppendLine();

        AppendStatusSection(builder, "TRIGGER");
        AppendProbeStatusRow(builder, "Mode", triggerMode);
        AppendProbeStatusRow(builder, "Conditions", triggerExpression.Conditions.Count.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Expression", triggerExpressionText);
        AppendProbeStatusRow(builder, "Matched Sample", _matchedTriggerSample?.ToString("N0", CultureInfo.InvariantCulture) ?? "-");
        AppendProbeStatusRow(builder, "Last Status", triggerExpression.IsEmpty ? "Immediate" : _probeCaptureState);
        builder.AppendLine();

        AppendStatusSection(builder, "QUALIFICATION");
        AppendProbeStatusRow(builder, "Mode", _qualificationMode == LogicQualificationMode.StoreWhenTrue ? "Store When True" : "Store All Samples");
        AppendProbeStatusRow(builder, "Condition", string.IsNullOrWhiteSpace(_qualificationConditionText) ? "-" : _qualificationConditionText);
        AppendProbeStatusRow(builder, "Stored Samples", _qualificationStoredSamples > 0 ? _qualificationStoredSamples.ToString("N0", CultureInfo.InvariantCulture) : "-");
        AppendProbeStatusRow(builder, "Skipped Samples", _qualificationSkippedSamples.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Timing", _qualificationTimingStatus);
        builder.AppendLine();

        AppendStatusSection(builder, "AUTO CAPTURE");
        AppendProbeStatusRow(builder, "Auto Re-trigger", _isAutoCaptureRunning ? "On" : "Off");
        AppendProbeStatusRow(builder, "Capture Limit", _autoCaptureLimit == 0 ? "Infinite" : _autoCaptureLimit.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Completed", _autoCapturesCompleted.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Current State", _autoCaptureState);
        AppendProbeStatusRow(builder, "Last Capture", FormatRelativeTime(_lastCaptureAt));
        builder.AppendLine();

        AppendStatusSection(builder, "MEASURE");
        AppendProbeStatusRow(builder, "Cursor A", FormatCursorSample(_cursorASample, _currentCapture));
        AppendProbeStatusRow(builder, "Cursor B", FormatCursorSample(_cursorBSample, _currentCapture));
        AppendProbeStatusRow(builder, "Δ Samples", FormatDeltaSamples(_cursorASample, _cursorBSample));
        AppendProbeStatusRow(builder, "Δ Time", FormatDeltaTime(_cursorASample, _cursorBSample, _currentCapture));
        AppendProbeStatusRow(builder, "Frequency", FormatDeltaFrequency(_cursorASample, _cursorBSample, _currentCapture));
        AppendProbeStatusRow(builder, "Pulse Width", "-");
        AppendProbeStatusRow(builder, "Duty Cycle", "-");
        builder.AppendLine();

        AppendStatusSection(builder, "SIGNALS");
        AppendProbeStatusRow(builder, "Channels", GetChannelCount().ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Named Signals", _signalDefinitions.Count(signal => !string.IsNullOrWhiteSpace(signal.Name)).ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Buses", _busDefinitions.Count.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Selected Signal", _selectedSignal);
        AppendProbeStatusRow(builder, "Radix", _busDefinitions.FirstOrDefault()?.Radix ?? _settings.DefaultBusRadix);
        builder.AppendLine();

        AppendStatusSection(builder, "FILE");
        AppendProbeStatusRow(builder, "Capture File", _captureFileName);
        AppendProbeStatusRow(builder, "Saved", _captureFileSaved ? "Yes" : "No");
        AppendProbeStatusRow(builder, "Format Version", "1");
        AppendProbeStatusRow(builder, "Last Saved", FormatRelativeTime(_lastSavedAt));
        builder.AppendLine();

        AppendStatusSection(builder, "PROTOCOL");
        AppendProbeStatusRow(builder, "Decoder", ProtocolAnalyzerPanel.SelectedProtocol);
        AppendProbeStatusRow(builder, "Decode Status", _decodeStatus);
        AppendProbeStatusRow(builder, "Decoded Frames", _decodedFrames.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Errors", _decodeErrors.ToString("N0", CultureInfo.InvariantCulture));
        AppendProbeStatusRow(builder, "Last Decode", FormatRelativeTime(_lastDecodeAt));
        builder.AppendLine();

        AppendStatusSection(builder, "ERRORS");
        AppendProbeStatusRow(builder, "Last Error", _probeLastError);

        ProbeFpgaStatusTextBlock.Text = builder.ToString();
    }

    private static void AppendProbeStatusRow(StringBuilder builder, string label, string value)
    {
        builder.Append(label.PadRight(18));
        builder.Append(": ");
        builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static void AppendStatusSection(StringBuilder builder, string title)
    {
        builder.Append(title);
        builder.Append(' ');
        builder.AppendLine(new string('-', Math.Max(1, 32 - title.Length)));
    }

    private static string FormatHertz(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz))
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        if (hz >= 1_000_000)
        {
            return $"{hz / 1_000_000:0.###} MHz";
        }

        if (hz >= 1_000)
        {
            return $"{hz / 1_000:0.###} kHz";
        }

        return $"{hz:0.###} Hz";
    }

    private string FormatRequestedRecordTime()
    {
        decimal value = RecordTimeNumericUpDown.Value ?? 10;
        return $"{value:0.###} {_recordTimeUnit}";
    }

    private static string FormatDurationSeconds(double seconds)
    {
        if (seconds < 0.001)
        {
            return $"{seconds * 1_000_000:0.###} µs";
        }

        if (seconds < 1)
        {
            return $"{seconds * 1000:0.###} ms";
        }

        return $"{seconds:0.###} s";
    }

    private static string FormatCursorSample(int? sample, LogicCapture? capture)
    {
        if (!sample.HasValue)
        {
            return "-";
        }

        if (capture == null || capture.SampleRateHz <= 0)
        {
            return $"sample {sample.Value:N0}";
        }

        return $"{FormatDurationSeconds(sample.Value / (double)capture.SampleRateHz)} / sample {sample.Value:N0}";
    }

    private static string FormatDeltaSamples(int? cursorA, int? cursorB)
    {
        if (!cursorA.HasValue || !cursorB.HasValue)
        {
            return "-";
        }

        return Math.Abs(cursorB.Value - cursorA.Value).ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatDeltaTime(int? cursorA, int? cursorB, LogicCapture? capture)
    {
        if (!cursorA.HasValue || !cursorB.HasValue || capture == null || capture.SampleRateHz <= 0)
        {
            return "-";
        }

        int deltaSamples = Math.Abs(cursorB.Value - cursorA.Value);
        return FormatDurationSeconds(deltaSamples / (double)capture.SampleRateHz);
    }

    private static string FormatDeltaFrequency(int? cursorA, int? cursorB, LogicCapture? capture)
    {
        if (!cursorA.HasValue || !cursorB.HasValue || capture == null || capture.SampleRateHz <= 0)
        {
            return "-";
        }

        int deltaSamples = Math.Abs(cursorB.Value - cursorA.Value);
        if (deltaSamples == 0)
        {
            return "-";
        }

        double frequency = capture.SampleRateHz / (double)deltaSamples;
        return FormatHertz(frequency.ToString(CultureInfo.InvariantCulture));
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

        return FormatDuration(elapsed) + " ago";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double kib = bytes / 1024.0;
        if (kib < 1024)
        {
            return kib.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return (kib / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    public void Dispose()
    {
        Disconnect();
    }

    private sealed record CapturePlan(
        int SampleRateHz,
        double RequestedSeconds,
        long RequestedSamples,
        int CaptureDepth,
        int ActualSamples,
        double ActualDurationSeconds,
        bool IsClamped);

    private sealed record TriggerConditionRowControls(
        Grid Grid,
        ComboBox SignalComboBox,
        ComboBox TypeComboBox,
        ComboBox OperatorComboBox,
        TextBox ValueTextBox,
        ComboBox EdgeComboBox);

    private sealed class LogicCaptureProject
    {
        public string Format { get; set; } = "LogicomCapture";

        public int Version { get; set; } = 1;

        public string AppVersion { get; set; } = "";

        public DateTime CreatedAt { get; set; }

        public int SampleRateHz { get; set; }

        public int Samples { get; set; }

        public int Channels { get; set; }

        public double RecordTimeSeconds { get; set; }

        public LogicTriggerExpression? Trigger { get; set; }

        public int TriggerPositionPercent { get; set; }

        public int? TriggerSampleIndex { get; set; }

        public string QualificationMode { get; set; } = "";

        public string QualificationCondition { get; set; } = "";

        public bool AutoRetriggerEnabled { get; set; }

        public string AutoCaptureLimit { get; set; } = "";

        public List<LogicSignalDefinition>? Signals { get; set; }

        public List<LogicBusDefinition>? Buses { get; set; }

        public string ProtocolDecoder { get; set; } = "Auto";

        public bool CursorSnapEnabled { get; set; }

        public LogicCaptureCursors? Cursors { get; set; }

        public string ProbeId { get; set; } = "";

        public string FirmwareVersion { get; set; } = "";

        public string BitstreamVersion { get; set; } = "";

        public string SampleData { get; set; } = "";
    }

    private sealed class LogicCaptureCursors
    {
        public int? A { get; set; }

        public int? B { get; set; }
    }
}
