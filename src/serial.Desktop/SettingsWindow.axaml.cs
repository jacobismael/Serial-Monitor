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
    private readonly List<ProbeRowControls> _probeRows = [];

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

        foreach (WaveformProbeDefinition probe in _mainWindow.WaveformProbes)
        {
            AddProbeRow(probe);
        }

        AddMacroButton.Click += (_, _) => AddMacroRow();
        AddProbeButton.Click += (_, _) => AddProbeRow();
        ApplyButton.Click += (_, _) => ApplySettings();
        CancelButton.Click += (_, _) => Close();
        BrowseLogSaveLocationButton.Click += async (_, _) => await BrowseLogSaveLocationAsync();

        HookTextBoxKeys(FontFamilyTextBox);
        HookTextBoxKeys(LogSaveLocationTextBox);
        HookTextBoxKeys(StartupCommandTextBox);
        HookTextBoxKeys(PlotterLineColorTextBox);
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
            MinHeight = 34
        };
        TextBox commandTextBox = new()
        {
            Text = macro?.Command ?? "",
            PlaceholderText = "reset or /path/to/tool --arg"
        };
        Button removeButton = new()
        {
            Content = "Remove",
            MinWidth = 82
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

    private void AddProbeRow(WaveformProbeDefinition? probe = null)
    {
        int rowNumber = _probeRows.Count + 1;
        TextBox probeTextBox = new()
        {
            Text = probe?.Probe ?? $"Probe {rowNumber}",
            PlaceholderText = $"Probe {rowNumber}"
        };
        TextBox signalTextBox = new()
        {
            Text = probe?.Signal ?? $"P{rowNumber}",
            PlaceholderText = "TXD"
        };
        TextBox colorTextBox = new()
        {
            Text = probe?.Color ?? LocalSettings.GetDefaultProbeColor(rowNumber - 1),
            PlaceholderText = "#00D4D8"
        };
        Border colorSwatch = new()
        {
            Width = 24,
            Height = 24,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = CreateColorBrush(colorTextBox.Text)
        };
        colorTextBox.TextChanged += (_, _) =>
        {
            colorSwatch.Background = CreateColorBrush(colorTextBox.Text);
        };
        Grid colorGrid = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(32))
            },
            ColumnSpacing = 8
        };
        Button removeButton = new()
        {
            Content = "Remove",
            MinWidth = 82
        };

        Grid rowGrid = CreateRowGrid(120, 120, 120);
        Grid.SetColumn(probeTextBox, 0);
        Grid.SetColumn(signalTextBox, 1);
        Grid.SetColumn(colorTextBox, 0);
        Grid.SetColumn(colorSwatch, 1);
        colorGrid.Children.Add(colorTextBox);
        colorGrid.Children.Add(colorSwatch);
        Grid.SetColumn(colorGrid, 2);
        Grid.SetColumn(removeButton, 3);
        rowGrid.Children.Add(probeTextBox);
        rowGrid.Children.Add(signalTextBox);
        rowGrid.Children.Add(colorGrid);
        rowGrid.Children.Add(removeButton);

        ProbeRowControls row = new(rowGrid, probeTextBox, signalTextBox, colorTextBox);
        removeButton.Click += (_, _) =>
        {
            ProbeRowsPanel.Children.Remove(rowGrid);
            _probeRows.Remove(row);
        };

        _probeRows.Add(row);
        ProbeRowsPanel.Children.Add(rowGrid);
        HookTextBoxKeys(probeTextBox);
        HookTextBoxKeys(signalTextBox);
        HookTextBoxKeys(colorTextBox);
    }

    private static Grid CreateRowGrid(double first, double second, double third = 0, bool starColumn = false)
    {
        Grid grid = new()
        {
            ColumnSpacing = 8
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(first)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(second)));
        grid.ColumnDefinitions.Add(starColumn
            ? new ColumnDefinition(new GridLength(1, GridUnitType.Star))
            : new ColumnDefinition(new GridLength(third)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        return grid;
    }

    private static Grid CreateMacroRowGrid()
    {
        Grid grid = new()
        {
            ColumnSpacing = 4
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(120)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(92)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(56)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(92)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

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
            _mainWindow.UpdateMacros(GetMacros());
            _mainWindow.UpdateWaveformProbes(GetWaveformProbes());
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
        PlotterLineColorTextBox.Text = settings.LineColor;
        PlotterAutoScaleCheckBox.IsChecked = settings.AutoScale;
        PlotterMinimumValueNumericUpDown.Value = (decimal)settings.MinimumValue;
        PlotterMaximumValueNumericUpDown.Value = (decimal)settings.MaximumValue;
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
            LineColor = PlotterLineColorTextBox.Text ?? "#00D4D8",
            AutoScale = PlotterAutoScaleCheckBox.IsChecked == true,
            MinimumValue = DecimalToDouble(PlotterMinimumValueNumericUpDown.Value, 0),
            MaximumValue = DecimalToDouble(PlotterMaximumValueNumericUpDown.Value, 100)
        };
        settings.Normalize();
        return settings;
    }

    private static int DecimalToInt(decimal? value, int fallback)
    {
        return value.HasValue ? decimal.ToInt32(value.Value) : fallback;
    }

    private static double DecimalToDouble(decimal? value, double fallback)
    {
        return value.HasValue ? decimal.ToDouble(value.Value) : fallback;
    }

    private IReadOnlyList<WaveformProbeDefinition> GetWaveformProbes()
    {
        List<WaveformProbeDefinition> probes = _probeRows
            .Select(row => new WaveformProbeDefinition
            {
                Probe = row.ProbeTextBox.Text ?? "",
                Signal = row.SignalTextBox.Text ?? "",
                Color = row.ColorTextBox.Text ?? ""
            })
            .ToList();

        return LocalSettings.NormalizeWaveformProbes(probes);
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

    private sealed record ProbeRowControls(
        Grid Grid,
        TextBox ProbeTextBox,
        TextBox SignalTextBox,
        TextBox ColorTextBox);
}
