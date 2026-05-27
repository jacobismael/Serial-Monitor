using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace serial.Desktop;

public partial class SettingsWindow : Window
{
    private readonly MainWindow? _mainWindow;
    private readonly List<MacroRowControls> _macroRows = [];
    private bool _syncingLogicAnalyzerSettings;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;

        InitializeComponent();
        Configure();
    }

    private void Configure()
    {
        if (_mainWindow == null)
        {
            return;
        }

        FontFamily = _mainWindow.FontFamily;
        FontFamilyTextBox.Text = _mainWindow.AppFontFamily;
        LogSaveLocationTextBox.Text = _mainWindow.DefaultLogSaveLocation;
        StartupCommandTextBox.Text = _mainWindow.StartupCommand;
        ConfigureStatusPanelSettings(_mainWindow.StatusPanelSettings);
        ConfigureSerialPlotterSettings(_mainWindow.SerialPlotterSettings);

        foreach (MacroDefinition macro in _mainWindow.Macros)
        {
            AddMacroRow(macro);
        }

        ConfigureLogicAnalyzerSettings();
        AddMacroButton.Click += (_, _) => AddMacroRow();
        ApplyButton.Click += (_, _) => ApplySettings();
        CancelButton.Click += (_, _) => Close();
        BrowseLogSaveLocationButton.Click += async (_, _) => await BrowseLogSaveLocationAsync();
        GeneralSettingsButton.Click += (_, _) => ShowSettingsSection(GeneralSettingsPanel);
        SerialPlotterSettingsButton.Click += (_, _) => ShowSettingsSection(SerialPlotterSettingsPanel);
        MacrosSettingsButton.Click += (_, _) => ShowSettingsSection(MacrosSettingsPanel);
        LogicAnalyzerSettingsButton.Click += (_, _) => ShowSettingsSection(LogicAnalyzerSettingsPanel);

        HookTextBoxKeys(FontFamilyTextBox);
        HookTextBoxKeys(LogSaveLocationTextBox);
        HookTextBoxKeys(StartupCommandTextBox);
        HookTextBoxKeys(PlotterLineColorTextBox);
        HookTextBoxKeys(PlotterSeriesColorsTextBox);
        ShowSettingsSection(GeneralSettingsPanel);
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        LogSaveLocationTextBox.Focus();
        LogSaveLocationTextBox.SelectAll();
    }

    private void AddMacroRow(MacroDefinition? macro = null)
    {
        TextBox nameTextBox = new()
        {
            Text = macro?.Name ?? "",
            PlaceholderText = "Reset"
        };
        ComboBox typeComboBox = new()
        {
            ItemsSource = new[] { MacroTypes.Serial, MacroTypes.Shell },
            SelectedItem = NormalizeMacroType(macro?.Type),
            MinHeight = 34
        };
        CheckBox showAsButtonCheckBox = new()
        {
            IsChecked = macro?.ShowAsButton != false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ComboBox endingComboBox = new()
        {
            ItemsSource = GetMacroEndingOptions(),
            SelectedItem = NormalizeMacroEnding(macro?.Ending),
            MinHeight = 34,
            MinWidth = 140
        };
        TextBox commandTextBox = new()
        {
            Text = macro?.Command ?? "",
            PlaceholderText = "reset or /path/to/tool --arg"
        };
        Button removeButton = new()
        {
            Content = "Remove",
            MinWidth = 110
        };

        Grid rowGrid = CreateMacroRowGrid();
        Grid.SetColumn(nameTextBox, 0);
        Grid.SetColumn(typeComboBox, 1);
        Grid.SetColumn(showAsButtonCheckBox, 2);
        Grid.SetColumn(commandTextBox, 3);
        Grid.SetColumn(endingComboBox, 4);
        Grid.SetColumn(removeButton, 5);
        rowGrid.Children.Add(nameTextBox);
        rowGrid.Children.Add(typeComboBox);
        rowGrid.Children.Add(showAsButtonCheckBox);
        rowGrid.Children.Add(commandTextBox);
        rowGrid.Children.Add(endingComboBox);
        rowGrid.Children.Add(removeButton);

        MacroRowControls row = new(rowGrid, nameTextBox, typeComboBox, showAsButtonCheckBox, endingComboBox, commandTextBox);
        removeButton.Click += (_, _) =>
        {
            MacroRowsPanel.Children.Remove(rowGrid);
            _macroRows.Remove(row);
        };

        _macroRows.Add(row);
        MacroRowsPanel.Children.Add(rowGrid);
        HookTextBoxKeys(nameTextBox);
        HookTextBoxKeys(commandTextBox);
    }

    private static Grid CreateMacroRowGrid()
    {
        Grid grid = new()
        {
            ColumnSpacing = 8
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(110)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(74)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(140)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(110)));

        return grid;
    }

    private static IBrush CreateColorBrush(string? colorText)
    {
        return TryParseHexColor(colorText, out Color color)
            ? new SolidColorBrush(color)
            : Brushes.Transparent;
    }

    private static bool TryParseHexColor(string? colorText, out Color color)
    {
        color = default;
        string value = (colorText ?? "").Trim();
        if (value.StartsWith("#", System.StringComparison.Ordinal))
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

    private void HookTextBoxKeys(TextBox textBox)
    {
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplySettings();
            }
            else if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private void ApplySettings()
    {
        if (_mainWindow != null)
        {
            _mainWindow.AppFontFamily = FontFamilyTextBox.Text ?? "";
            _mainWindow.DefaultLogSaveLocation = LogSaveLocationTextBox.Text ?? "";
            _mainWindow.StartupCommand = StartupCommandTextBox.Text ?? "";
            _mainWindow.UpdateStatusPanelSettings(GetStatusPanelSettings());
            _mainWindow.UpdateSerialPlotterSettings(GetSerialPlotterSettings());
            _mainWindow.UpdateLogicAnalyzerSettings(GetLogicAnalyzerSettings());
            _mainWindow.UpdateMacros(GetMacros());
        }

        Close();
    }

    private IReadOnlyList<MacroDefinition> GetMacros()
    {
        return _macroRows
            .Select(row => new MacroDefinition
            {
                Name = row.NameTextBox.Text ?? "",
                Command = row.CommandTextBox.Text ?? "",
                Type = NormalizeMacroType(row.TypeComboBox.SelectedItem as string),
                Ending = NormalizeMacroEnding(row.EndingComboBox.SelectedItem as string),
                ShowAsButton = row.ShowAsButtonCheckBox.IsChecked == true
            })
            .ToList();
    }

    private void ConfigureStatusPanelSettings(StatusPanelSettings settings)
    {
        settings.Normalize();

        StatusDataBitsComboBox.ItemsSource = new[] { "5", "6", "7", "8" };
        StatusParityComboBox.ItemsSource = new[] { "None", "Even", "Odd", "Mark", "Space" };
        StatusStopBitsComboBox.ItemsSource = new[] { "1", "1.5", "2" };
        StatusFlowControlComboBox.ItemsSource = new[] { "None", "RTS/CTS", "XON/XOFF" };

        StatusDataBitsComboBox.SelectedItem = settings.DataBits;
        StatusParityComboBox.SelectedItem = settings.Parity;
        StatusStopBitsComboBox.SelectedItem = settings.StopBits;
        StatusFlowControlComboBox.SelectedItem = settings.FlowControl;
    }

    private StatusPanelSettings GetStatusPanelSettings()
    {
        StatusPanelSettings settings = new()
        {
            DataBits = StatusDataBitsComboBox.SelectedItem as string ?? "8",
            Parity = StatusParityComboBox.SelectedItem as string ?? "None",
            StopBits = StatusStopBitsComboBox.SelectedItem as string ?? "1",
            FlowControl = StatusFlowControlComboBox.SelectedItem as string ?? "None"
        };
        settings.Normalize();
        return settings;
    }

    private void ConfigureSerialPlotterSettings(SerialPlotterSettings settings)
    {
        settings.Normalize();

        PlotterMaxSamplesNumericUpDown.Value = settings.MaxSamples;
        PlotterVisibleSamplesNumericUpDown.Value = settings.VisibleSamples;
        PlotterSaveFullHistoryCheckBox.IsChecked = settings.SaveFullHistory;
        PlotterMaxHistorySamplesNumericUpDown.Value = settings.MaxHistorySamples;
        PlotterXAxisModeComboBox.ItemsSource = new[] { "Samples", "Time" };
        PlotterXAxisModeComboBox.SelectedItem = settings.XAxisMode;
        PlotterLineColorTextBox.Text = settings.LineColor;
        PlotterSeriesColorsTextBox.Text = string.Join(",", settings.SeriesColors);
        PlotterAutoScaleCheckBox.IsChecked = settings.AutoScale;
        PlotterShowGridCheckBox.IsChecked = settings.ShowGrid;
        PlotterShowPointsCheckBox.IsChecked = settings.ShowPoints;
        PlotterLineThicknessNumericUpDown.Value = (decimal)settings.LineThickness;
        PlotterMinimumValueNumericUpDown.Value = (decimal)settings.MinimumValue;
        PlotterMaximumValueNumericUpDown.Value = (decimal)settings.MaximumValue;
        PlotterCsvExportModeComboBox.ItemsSource = new[] { "Visible", "Full History" };
        PlotterCsvExportModeComboBox.SelectedItem = settings.CsvExportMode;
        PlotterParserModeComboBox.ItemsSource = new[] { "Auto", "Single Value", "Key-Value", "CSV" };
        PlotterParserModeComboBox.SelectedItem = settings.ParserMode;
        PlotterLineColorSwatch.Background = CreateColorBrush(settings.LineColor);

        PlotterLineColorTextBox.TextChanged += (_, _) =>
        {
            PlotterLineColorSwatch.Background = CreateColorBrush(PlotterLineColorTextBox.Text);
        };
    }

    private SerialPlotterSettings GetSerialPlotterSettings()
    {
        SerialPlotterSettings settings = new()
        {
            MaxSamples = DecimalToInt(PlotterMaxSamplesNumericUpDown.Value, 400),
            VisibleSamples = DecimalToInt(PlotterVisibleSamplesNumericUpDown.Value, 160),
            SaveFullHistory = PlotterSaveFullHistoryCheckBox.IsChecked == true,
            MaxHistorySamples = DecimalToInt(PlotterMaxHistorySamplesNumericUpDown.Value, 100000),
            XAxisMode = PlotterXAxisModeComboBox.SelectedItem as string ?? "Samples",
            LineColor = PlotterLineColorTextBox.Text ?? "#00D4D8",
            SeriesColors = ParseColorList(PlotterSeriesColorsTextBox.Text),
            AutoScale = PlotterAutoScaleCheckBox.IsChecked == true,
            ShowGrid = PlotterShowGridCheckBox.IsChecked == true,
            ShowPoints = PlotterShowPointsCheckBox.IsChecked == true,
            LineThickness = DecimalToDouble(PlotterLineThicknessNumericUpDown.Value, 1.5),
            MinimumValue = DecimalToDouble(PlotterMinimumValueNumericUpDown.Value, 0),
            MaximumValue = DecimalToDouble(PlotterMaximumValueNumericUpDown.Value, 100),
            CsvExportMode = PlotterCsvExportModeComboBox.SelectedItem as string ?? "Visible",
            ParserMode = PlotterParserModeComboBox.SelectedItem as string ?? "Auto"
        };
        settings.Normalize();
        return settings;
    }

    private static List<string> ParseColorList(string? value)
    {
        return (value ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .ToList();
    }

    private void ConfigureLogicAnalyzerSettings()
    {
        if (_mainWindow == null)
        {
            return;
        }

        LogicAnalyzerControl analyzer = _mainWindow.LogicAnalyzerControl;
        LogicAnalyzerSettings settings = _mainWindow.LogicAnalyzerSettings;
        settings.Normalize();
        LogicAnalyzerBaudComboBox.ItemsSource = analyzer.BaudRates;
        LogicAnalyzerBaudComboBox.SelectedItem = analyzer.SelectedBaudRate;
        LogicAnalyzerShowStatusCheckBox.IsChecked = settings.ShowFpgaStatusPanel;
        LogicAnalyzerShowProtocolCheckBox.IsChecked = settings.ShowProtocolAnalyzer;
        LogicAnalyzerSampleRateTextBox.Text = settings.DefaultSampleRate.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerChannelCountTextBox.Text = settings.DefaultChannelCount.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerTriggerChannelTextBox.Text = settings.DefaultTriggerChannel.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerTriggerModeTextBox.Text = settings.DefaultTriggerMode;
        LogicAnalyzerCaptureDepthTextBox.Text = settings.CaptureDepth.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerZoomSensitivityTextBox.Text = settings.ZoomSensitivity.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerTriggerPositionTextBox.Text = settings.DefaultTriggerPositionPercent.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerTriggerCombineComboBox.ItemsSource = new[] { "AND", "OR" };
        LogicAnalyzerTriggerCombineComboBox.SelectedItem = settings.DefaultTriggerCombineMode;
        LogicAnalyzerQualificationModeComboBox.ItemsSource = new[] { "Store All Samples", "Store When Condition True" };
        LogicAnalyzerQualificationModeComboBox.SelectedItem = settings.DefaultCaptureQualificationMode;
        LogicAnalyzerAutoRetriggerCheckBox.IsChecked = settings.EnableAutoRetriggerByDefault;
        LogicAnalyzerAutoCaptureLimitComboBox.ItemsSource = new[] { "Infinite", "1", "5", "10", "100" };
        LogicAnalyzerAutoCaptureLimitComboBox.SelectedItem = settings.DefaultAutoCaptureLimit;
        LogicAnalyzerCursorSnapCheckBox.IsChecked = settings.DefaultCursorSnapMode;
        LogicAnalyzerBusRadixComboBox.ItemsSource = new[] { "Binary", "Hex", "Unsigned", "Signed" };
        LogicAnalyzerBusRadixComboBox.SelectedItem = settings.DefaultBusRadix;
        LogicAnalyzerSaveDecodedFramesCheckBox.IsChecked = settings.SaveDecodedFramesInCaptureFile;
        LogicAnalyzerSaveAnnotationsCheckBox.IsChecked = settings.SaveAnnotationsInCaptureFile;
        LogicAnalyzerProtocolDecoderComboBox.ItemsSource = new[] { "Auto", "UART", "I2C", "SPI", "CAN" };
        LogicAnalyzerProtocolDecoderComboBox.SelectedItem = settings.DefaultProtocolDecoder;
        LogicAnalyzerUartBaudTextBox.Text = settings.DefaultUartBaud.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerCanBaudTextBox.Text = settings.DefaultCanBaud.ToString(CultureInfo.InvariantCulture);
        LogicAnalyzerI2cChannelsTextBox.Text = $"{settings.DefaultI2cSdaChannel},{settings.DefaultI2cSclChannel}";
        LogicAnalyzerSpiChannelsTextBox.Text = string.Join(
            ",",
            settings.DefaultSpiSclkChannel,
            settings.DefaultSpiMosiChannel,
            settings.DefaultSpiMisoChannel,
            settings.DefaultSpiCsChannel);
        RefreshLogicAnalyzerPorts();
        UpdateLogicAnalyzerConnectionControls();

        LogicAnalyzerPortComboBox.SelectionChanged += (_, _) =>
        {
            if (_syncingLogicAnalyzerSettings || analyzer.IsConnected)
            {
                return;
            }

            analyzer.SelectedPort = LogicAnalyzerPortComboBox.SelectedItem as string;
        };
        LogicAnalyzerBaudComboBox.SelectionChanged += (_, _) =>
        {
            if (_syncingLogicAnalyzerSettings || analyzer.IsConnected)
            {
                return;
            }

            if (LogicAnalyzerBaudComboBox.SelectedItem is int baudRate)
            {
                analyzer.SelectedBaudRate = baudRate;
            }
        };
        LogicAnalyzerRefreshPortsButton.Click += (_, _) => RefreshLogicAnalyzerPorts();
        LogicAnalyzerConnectButton.Click += (_, _) =>
        {
            analyzer.ToggleConnectionFromSettings();
            RefreshLogicAnalyzerPorts();
            UpdateLogicAnalyzerConnectionControls();
        };
    }

    private void RefreshLogicAnalyzerPorts()
    {
        if (_mainWindow == null)
        {
            return;
        }

        LogicAnalyzerControl analyzer = _mainWindow.LogicAnalyzerControl;
        _syncingLogicAnalyzerSettings = true;
        string? selectedPort = analyzer.SelectedPort;
        string[] ports = analyzer.RefreshPorts(notify: false);
        LogicAnalyzerPortComboBox.ItemsSource = ports;
        if (!string.IsNullOrWhiteSpace(selectedPort) && ports.Contains(selectedPort))
        {
            LogicAnalyzerPortComboBox.SelectedItem = selectedPort;
        }
        else
        {
            LogicAnalyzerPortComboBox.SelectedItem = analyzer.SelectedPort;
        }

        LogicAnalyzerBaudComboBox.SelectedItem = analyzer.SelectedBaudRate;
        _syncingLogicAnalyzerSettings = false;
    }

    private void UpdateLogicAnalyzerConnectionControls()
    {
        if (_mainWindow == null)
        {
            return;
        }

        bool isConnected = _mainWindow.LogicAnalyzerControl.IsConnected;
        LogicAnalyzerConnectButton.Content = isConnected ? "Disconnect" : "Connect";
        LogicAnalyzerPortComboBox.IsEnabled = !isConnected;
        LogicAnalyzerBaudComboBox.IsEnabled = !isConnected;
        LogicAnalyzerRefreshPortsButton.IsEnabled = !isConnected;
    }

    private LogicAnalyzerSettings GetLogicAnalyzerSettings()
    {
        LogicAnalyzerSettings settings = new()
        {
            ShowFpgaStatusPanel = LogicAnalyzerShowStatusCheckBox.IsChecked == true,
            ShowProtocolAnalyzer = LogicAnalyzerShowProtocolCheckBox.IsChecked == true,
            DefaultSampleRate = ParseInt(LogicAnalyzerSampleRateTextBox.Text, 1000000),
            DefaultChannelCount = ParseInt(LogicAnalyzerChannelCountTextBox.Text, 8),
            DefaultTriggerChannel = ParseInt(LogicAnalyzerTriggerChannelTextBox.Text, 0),
            DefaultTriggerMode = LogicAnalyzerTriggerModeTextBox.Text ?? "None",
            DefaultProtocolDecoder = LogicAnalyzerProtocolDecoderComboBox.SelectedItem as string ?? "Auto",
            DefaultUartBaud = ParseInt(LogicAnalyzerUartBaudTextBox.Text, 115200),
            DefaultCanBaud = ParseInt(LogicAnalyzerCanBaudTextBox.Text, 500000),
            CaptureDepth = ParseInt(LogicAnalyzerCaptureDepthTextBox.Text, 8192),
            ZoomSensitivity = ParseDouble(LogicAnalyzerZoomSensitivityTextBox.Text, 1.08),
            DefaultTriggerPositionPercent = ParseInt(LogicAnalyzerTriggerPositionTextBox.Text, 50),
            DefaultTriggerCombineMode = LogicAnalyzerTriggerCombineComboBox.SelectedItem as string ?? "AND",
            DefaultCaptureQualificationMode = LogicAnalyzerQualificationModeComboBox.SelectedItem as string ?? "Store All Samples",
            EnableAutoRetriggerByDefault = LogicAnalyzerAutoRetriggerCheckBox.IsChecked == true,
            DefaultAutoCaptureLimit = LogicAnalyzerAutoCaptureLimitComboBox.SelectedItem as string ?? "Infinite",
            DefaultCursorSnapMode = LogicAnalyzerCursorSnapCheckBox.IsChecked == true,
            DefaultBusRadix = LogicAnalyzerBusRadixComboBox.SelectedItem as string ?? "Hex",
            SaveDecodedFramesInCaptureFile = LogicAnalyzerSaveDecodedFramesCheckBox.IsChecked == true,
            SaveAnnotationsInCaptureFile = LogicAnalyzerSaveAnnotationsCheckBox.IsChecked == true
        };

        int[] i2cChannels = ParseIntList(LogicAnalyzerI2cChannelsTextBox.Text, [5, 4]);
        settings.DefaultI2cSdaChannel = i2cChannels.ElementAtOrDefault(0);
        settings.DefaultI2cSclChannel = i2cChannels.ElementAtOrDefault(1);

        int[] spiChannels = ParseIntList(LogicAnalyzerSpiChannelsTextBox.Text, [6, 7, -1, -1]);
        settings.DefaultSpiSclkChannel = spiChannels.ElementAtOrDefault(0);
        settings.DefaultSpiMosiChannel = spiChannels.ElementAtOrDefault(1);
        settings.DefaultSpiMisoChannel = spiChannels.ElementAtOrDefault(2);
        settings.DefaultSpiCsChannel = spiChannels.ElementAtOrDefault(3);

        settings.Normalize();
        return settings;
    }

    private void ShowSettingsSection(Control visiblePanel)
    {
        GeneralSettingsPanel.IsVisible = visiblePanel == GeneralSettingsPanel;
        SerialPlotterSettingsPanel.IsVisible = visiblePanel == SerialPlotterSettingsPanel;
        MacrosSettingsPanel.IsVisible = visiblePanel == MacrosSettingsPanel;
        LogicAnalyzerSettingsPanel.IsVisible = visiblePanel == LogicAnalyzerSettingsPanel;

        GeneralSettingsButton.Opacity = GeneralSettingsPanel.IsVisible ? 1 : 0.65;
        SerialPlotterSettingsButton.Opacity = SerialPlotterSettingsPanel.IsVisible ? 1 : 0.65;
        MacrosSettingsButton.Opacity = MacrosSettingsPanel.IsVisible ? 1 : 0.65;
        LogicAnalyzerSettingsButton.Opacity = LogicAnalyzerSettingsPanel.IsVisible ? 1 : 0.65;
    }

    private static int DecimalToInt(decimal? value, int fallback)
    {
        return value.HasValue ? decimal.ToInt32(value.Value) : fallback;
    }

    private static double DecimalToDouble(decimal? value, double fallback)
    {
        return value.HasValue ? decimal.ToDouble(value.Value) : fallback;
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    private static double ParseDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
    }

    private static int[] ParseIntList(string? value, int[] fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        int[] parsed = value
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Select(part => ParseInt(part, int.MinValue))
            .Where(number => number != int.MinValue)
            .ToArray();

        return parsed.Length == 0 ? fallback : parsed;
    }

    private static string NormalizeMacroType(string? macroType)
    {
        return string.Equals(macroType, MacroTypes.Shell, System.StringComparison.OrdinalIgnoreCase)
            ? MacroTypes.Shell
            : MacroTypes.Serial;
    }

    private static string[] GetMacroEndingOptions()
    {
        return
        [
            MacroEndingTypes.Current,
            MacroEndingTypes.None,
            MacroEndingTypes.LF,
            MacroEndingTypes.CR,
            MacroEndingTypes.CRLF
        ];
    }

    private static string NormalizeMacroEnding(string? ending)
    {
        return GetMacroEndingOptions().Contains(ending)
            ? ending!
            : MacroEndingTypes.Current;
    }

    private async System.Threading.Tasks.Task BrowseLogSaveLocationAsync()
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        IStorageFolder? suggestedStartLocation = null;
        string currentLocation = LogSaveLocationTextBox.Text ?? "";
        if (!string.IsNullOrWhiteSpace(currentLocation))
        {
            suggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(currentLocation);
        }

        IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Default Log Save Location",
                AllowMultiple = false,
                SuggestedStartLocation = suggestedStartLocation
            });

        IStorageFolder? folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is string path && !string.IsNullOrWhiteSpace(path))
        {
            LogSaveLocationTextBox.Text = path;
        }
    }

    private sealed record MacroRowControls(
        Grid Grid,
        TextBox NameTextBox,
        ComboBox TypeComboBox,
        CheckBox ShowAsButtonCheckBox,
        ComboBox EndingComboBox,
        TextBox CommandTextBox);
}
