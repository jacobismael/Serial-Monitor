using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using serial.Core;
using Avalonia.VisualTree;

namespace serial.Desktop;

public partial class MainWindow : Window
{
    private const string AppTitle = "Logicom";
    private SettingsWindow? _settingsWindow;
    private LocalSettings _settings = new();
    private AppMode _activeMode = AppMode.SerialMonitor;

    public event Action? NewWindowRequested;
    public event Action<bool>? TimestampsToggled;
    public event Action<bool>? SignalViewerToggled;
    public event Action<bool>? SignalViewerDetachedToggled;
    public event Action<bool>? StatusPanelDetachedToggled;
    public event Action<bool>? SerialPlotterToggled;

    private SessionControl _mainSessionControl = null!;
    private LogicAnalyzerControl _logicAnalyzerControl = null!;
    private SerialPlotterControl _plotterControl = null!;
    public SessionControl ActiveSessionControl => _mainSessionControl;
    public LogicAnalyzerControl LogicAnalyzerControl => _logicAnalyzerControl;

    public string AppFontFamily
    {
        get => _settings.FontFamily;
        set
        {
            string fontFamily = string.IsNullOrWhiteSpace(value)
                ? LocalSettings.DefaultFontFamily
                : value.Trim();

            fontFamily = fontFamily.Replace("compositefont:", "", StringComparison.OrdinalIgnoreCase);

            FontFamily = new FontFamily(fontFamily);
            _settingsWindow?.SetValue(FontFamilyProperty, FontFamily);
            _settings.FontFamily = fontFamily;
            LocalSettings.Save(_settings);
        }
    }

    public string DefaultLogSaveLocation
    {
        get => _settings.DefaultLogSaveLocation;
        set
        {
            _settings.DefaultLogSaveLocation = value.Trim();
            LocalSettings.Save(_settings);
        }
    }

    public string StartupCommand
    {
        get => _settings.StartupCommand;
        set
        {
            _settings.StartupCommand = value.Trim();
            LocalSettings.Save(_settings);
        }
    }

    public IReadOnlyList<MacroDefinition> Macros => _settings.Macros;

    public IReadOnlyList<WaveformProbeDefinition> WaveformProbes => _settings.WaveformProbes;
    public StatusPanelSettings StatusPanelSettings => _settings.StatusPanel;
    public SerialPlotterSettings SerialPlotterSettings => _settings.SerialPlotter;
    public LogicAnalyzerSettings LogicAnalyzerSettings => _settings.LogicAnalyzer;
    public bool TimestampsEnabled => _mainSessionControl.TimestampsEnabled;
    public bool IsSignalViewerVisible => false;
    public bool IsSignalViewerDetached => false;
    public bool IsStatusPanelDetached => _mainSessionControl.IsStatusPanelDetached;
    public bool IsSerialPlotterVisible => _activeMode == AppMode.Plotter;

    public MainWindow()
    {
        InitializeComponent();

        _mainSessionControl = this.FindControl<SessionControl>("MainSessionControl")
            ?? throw new InvalidOperationException("MainSessionControl not found.");
        _logicAnalyzerControl = this.FindControl<LogicAnalyzerControl>("MainLogicAnalyzerControl")
            ?? throw new InvalidOperationException("MainLogicAnalyzerControl not found.");
        _plotterControl = this.FindControl<SerialPlotterControl>("MainPlotterControl")
            ?? throw new InvalidOperationException("MainPlotterControl not found.");

        _settings = LocalSettings.Load();
        AppFontFamily = _settings.FontFamily;

        DataContext = this;
        _mainSessionControl.UpdateMacros(_settings.Macros);
        _mainSessionControl.UpdateStatusPanelSettings(_settings.StatusPanel);
        _mainSessionControl.UpdateSerialPlotterSettings(_settings.SerialPlotter);
        _plotterControl.UpdateSettings(_settings.SerialPlotter);
        _logicAnalyzerControl.UpdateSettings(_settings.LogicAnalyzer);
        _mainSessionControl.RunStartupCommand(_settings.StartupCommand);
        _mainSessionControl.SignalViewerDetachedChanged += detached =>
        {
            SignalViewerDetachedToggled?.Invoke(detached);
            SignalViewerToggled?.Invoke(_mainSessionControl.IsSignalViewerVisible);
        };
        _mainSessionControl.StatusPanelDetachedChanged += detached =>
        {
            StatusPanelDetachedToggled?.Invoke(detached);
        };
        _mainSessionControl.SerialPlotterToggled += visible =>
        {
            SerialPlotterToggled?.Invoke(visible);
        };
        _mainSessionControl.SerialDataReceived += data =>
        {
            _plotterControl.ProcessSerialData(data);
        };
        _mainSessionControl.UtilityWindowCreated += window =>
        {
            (Application.Current as App)?.AttachWindowMenu(window, this);
        };
        _mainSessionControl.ConnectionChanged += (portName, baudRate) =>
        {
            UpdateTitle(portName, baudRate);
            _plotterControl.UpdateSerialConnection(portName, baudRate);
        };
        _mainSessionControl.StatusSettingsRequested += ShowSettingsWindow;
        SerialMonitorModeButton.Click += (_, _) => SetMode(AppMode.SerialMonitor);
        PlotterModeButton.Click += (_, _) => SetMode(AppMode.Plotter);
        LogicAnalyzerModeButton.Click += (_, _) => SetMode(AppMode.LogicAnalyzer);
        SetMode(AppMode.SerialMonitor);
        UpdateTitle(null, null);
        TimestampsToggled?.Invoke(_mainSessionControl.TimestampsEnabled);

        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                e.Handled = true;
                await ShowFindWindowAsync();
            }
            else if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                ToggleTimestamps();
            }
            else if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                e.Handled = true;
                NewWindowRequested?.Invoke();
            }
        };
    }

    public void UpdateMacros(IEnumerable<MacroDefinition> macros)
    {
        _settings.Macros = macros
            .Where(macro => !string.IsNullOrWhiteSpace(macro.Name) || !string.IsNullOrWhiteSpace(macro.Command))
            .Select(macro => new MacroDefinition
            {
                Name = macro.Name.Trim(),
                Command = macro.Command.Trim(),
                Type = string.Equals(macro.Type, MacroTypes.Shell, StringComparison.OrdinalIgnoreCase)
                    ? MacroTypes.Shell
                    : MacroTypes.Serial,
                Ending = string.IsNullOrWhiteSpace(macro.Ending) ? MacroEndingTypes.Current : macro.Ending.Trim(),
                ShowAsButton = macro.ShowAsButton
            })
            .ToList();

        LocalSettings.Save(_settings);
        _mainSessionControl.UpdateMacros(_settings.Macros);
    }

    public void UpdateWaveformProbes(IEnumerable<WaveformProbeDefinition> probes)
    {
        _settings.WaveformProbes = LocalSettings.NormalizeWaveformProbes(probes);
        LocalSettings.Save(_settings);
    }

    public void UpdateStatusPanelSettings(StatusPanelSettings statusPanelSettings)
    {
        statusPanelSettings.Normalize();
        _settings.StatusPanel = statusPanelSettings;
        LocalSettings.Save(_settings);
        _mainSessionControl.UpdateStatusPanelSettings(_settings.StatusPanel);
    }

    public void UpdateSerialPlotterSettings(SerialPlotterSettings serialPlotterSettings)
    {
        serialPlotterSettings.Normalize();
        _settings.SerialPlotter = serialPlotterSettings;
        LocalSettings.Save(_settings);
        _mainSessionControl.UpdateSerialPlotterSettings(_settings.SerialPlotter);
        _plotterControl.UpdateSettings(_settings.SerialPlotter);
    }

    public void UpdateLogicAnalyzerSettings(LogicAnalyzerSettings logicAnalyzerSettings)
    {
        logicAnalyzerSettings.Normalize();
        _settings.LogicAnalyzer = logicAnalyzerSettings;
        LocalSettings.Save(_settings);
        _logicAnalyzerControl.UpdateSettings(_settings.LogicAnalyzer);
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
        };
        (Application.Current as App)?.AttachWindowMenu(_settingsWindow, this);
        _settingsWindow.Show(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _settingsWindow?.Close();
        _mainSessionControl.Dispose();
        _logicAnalyzerControl.Dispose();
        base.OnClosed(e);
    }

    public async Task ShowFindWindowAsync()
    {
        if (_activeMode != AppMode.SerialMonitor)
        {
            return;
        }

        await ActiveSessionControl.ShowFindWindowAsync();
    }

    public async Task SaveLogAsync()
    {
        if (_activeMode != AppMode.SerialMonitor)
        {
            return;
        }

        await ActiveSessionControl.SaveLogAsync();
    }

    public async Task SaveLogicCaptureAsync()
    {
        if (_activeMode != AppMode.LogicAnalyzer)
        {
            return;
        }

        await _logicAnalyzerControl.SaveCaptureProjectAsync();
    }

    public async Task OpenLogicCaptureAsync()
    {
        SetMode(AppMode.LogicAnalyzer);
        await _logicAnalyzerControl.OpenCaptureProjectAsync();
    }

    public async Task ExportLogicCaptureCsvAsync()
    {
        if (_activeMode != AppMode.LogicAnalyzer)
        {
            return;
        }

        await _logicAnalyzerControl.ExportCaptureAsync("csv");
    }

    public async Task ExportLogicCaptureVcdAsync()
    {
        if (_activeMode != AppMode.LogicAnalyzer)
        {
            return;
        }

        await _logicAnalyzerControl.ExportCaptureAsync("vcd");
    }

    public async Task ExportLogicDecodedCsvAsync()
    {
        if (_activeMode != AppMode.LogicAnalyzer)
        {
            return;
        }

        await _logicAnalyzerControl.ExportDecodedCsvAsync();
    }

    public async Task CopySerialMonitorAsync()
    {
        if (_activeMode != AppMode.SerialMonitor)
        {
            return;
        }

        await ActiveSessionControl.CopySerialMonitorAsync();
    }

    public bool ToggleTimestamps()
    {
        if (_activeMode != AppMode.SerialMonitor)
        {
            return false;
        }

        bool enabled = ActiveSessionControl.ToggleTimestamps();
        TimestampsToggled?.Invoke(enabled);
        return enabled;
    }

    public bool ToggleSignalViewer()
    {
        return false;
    }

    public bool ToggleSignalViewerDetached()
    {
        return false;
    }

    public void ShowStatusPanel()
    {
        if (_activeMode != AppMode.SerialMonitor)
        {
            return;
        }

        ActiveSessionControl.ShowStatusPanel();
    }

    public void ShowPlotterStatusPanel()
    {
        _plotterControl.ShowStatusPanel();
    }

    public void ShowLogicAnalyzerProbeStatusPanel()
    {
        _logicAnalyzerControl.ShowProbeStatusPanel();
    }

    public void ShowProtocolAnalyzer()
    {
        _logicAnalyzerControl.ShowProtocolAnalyzer();
    }

    public bool ToggleStatusPanelDetached()
    {
        if (_activeMode != AppMode.SerialMonitor)
        {
            return false;
        }

        bool detached = ActiveSessionControl.ToggleStatusPanelDetached();
        StatusPanelDetachedToggled?.Invoke(detached);
        return detached;
    }

    public void ShowSerialPlotter()
    {
        SetMode(AppMode.Plotter);
        SerialPlotterToggled?.Invoke(true);
    }

    private void UpdateTitle(string? portName, int? baudRate)
    {
        Title = !string.IsNullOrWhiteSpace(portName) && baudRate.HasValue
            ? $"{AppTitle} - {portName} @ {baudRate.Value}"
            : AppTitle;
    }

    private void SetMode(AppMode mode)
    {
        _activeMode = mode;
        MainSessionControl.IsVisible = mode == AppMode.SerialMonitor;
        MainPlotterControl.IsVisible = mode == AppMode.Plotter;
        MainLogicAnalyzerControl.IsVisible = mode == AppMode.LogicAnalyzer;
        UpdateModeButtonState();
    }

    private void UpdateModeButtonState()
    {
        SerialMonitorModeButton.Opacity = _activeMode == AppMode.SerialMonitor ? 1 : 0.65;
        PlotterModeButton.Opacity = _activeMode == AppMode.Plotter ? 1 : 0.65;
        LogicAnalyzerModeButton.Opacity = _activeMode == AppMode.LogicAnalyzer ? 1 : 0.65;
    }

    private enum AppMode
    {
        SerialMonitor,
        Plotter,
        LogicAnalyzer
    }
}
