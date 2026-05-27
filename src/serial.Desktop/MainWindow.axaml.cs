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

    public event Action? NewWindowRequested;
    public event Action<bool>? TimestampsToggled;
    public event Action<bool>? SignalViewerToggled;
    public event Action<bool>? SignalViewerDetachedToggled;
    public event Action<bool>? StatusPanelDetachedToggled;
    public event Action<bool>? SerialPlotterToggled;

    private SessionControl _mainSessionControl = null!;
    public SessionControl ActiveSessionControl => _mainSessionControl;

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
    public bool TimestampsEnabled => _mainSessionControl.TimestampsEnabled;
    public bool IsSignalViewerVisible => _mainSessionControl.IsSignalViewerVisible;
    public bool IsSignalViewerDetached => _mainSessionControl.IsSignalViewerDetached;
    public bool IsStatusPanelDetached => _mainSessionControl.IsStatusPanelDetached;
    public bool IsSerialPlotterVisible => _mainSessionControl.IsSerialPlotterVisible;

    public MainWindow()
    {
        InitializeComponent();

        _mainSessionControl = this.FindControl<SessionControl>("MainSessionControl")
            ?? throw new InvalidOperationException("MainSessionControl not found.");

        _settings = LocalSettings.Load();
        AppFontFamily = _settings.FontFamily;

        DataContext = this;
        _mainSessionControl.UpdateMacros(_settings.Macros);
        _mainSessionControl.UpdateWaveformProbes(_settings.WaveformProbes);
        _mainSessionControl.UpdateStatusPanelSettings(_settings.StatusPanel);
        _mainSessionControl.UpdateSerialPlotterSettings(_settings.SerialPlotter);
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
        _mainSessionControl.UtilityWindowCreated += window =>
        {
            (Application.Current as App)?.AttachWindowMenu(window, this);
        };
        _mainSessionControl.ConnectionChanged += UpdateTitle;
        _mainSessionControl.StatusSettingsRequested += ShowSettingsWindow;
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
            else if (e.Key == Key.V
                && e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                ToggleSignalViewer();
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
        _mainSessionControl.UpdateWaveformProbes(_settings.WaveformProbes);
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
        base.OnClosed(e);
    }

    public async Task ShowFindWindowAsync()
    {
        await ActiveSessionControl.ShowFindWindowAsync();
    }

    public async Task SaveLogAsync()
    {
        await ActiveSessionControl.SaveLogAsync();
    }

    public async Task CopySerialMonitorAsync()
    {
        await ActiveSessionControl.CopySerialMonitorAsync();
    }

    public bool ToggleTimestamps()
    {
        bool enabled = ActiveSessionControl.ToggleTimestamps();
        TimestampsToggled?.Invoke(enabled);
        return enabled;
    }

    public bool ToggleSignalViewer()
    {
        bool enabled = ActiveSessionControl.ToggleSignalViewer();
        SignalViewerToggled?.Invoke(enabled);
        return enabled;
    }

    public bool ToggleSignalViewerDetached()
    {
        bool detached = ActiveSessionControl.ToggleSignalViewerDetached();
        SignalViewerDetachedToggled?.Invoke(detached);
        SignalViewerToggled?.Invoke(ActiveSessionControl.IsSignalViewerVisible);
        return detached;
    }

    public void ShowStatusPanel()
    {
        ActiveSessionControl.ShowStatusPanel();
    }

    public bool ToggleStatusPanelDetached()
    {
        bool detached = ActiveSessionControl.ToggleStatusPanelDetached();
        StatusPanelDetachedToggled?.Invoke(detached);
        return detached;
    }

    public void ShowSerialPlotter()
    {
        ActiveSessionControl.ShowSerialPlotter();
        SerialPlotterToggled?.Invoke(ActiveSessionControl.IsSerialPlotterVisible);
    }

    private void UpdateTitle(string? portName, int? baudRate)
    {
        Title = !string.IsNullOrWhiteSpace(portName) && baudRate.HasValue
            ? $"{AppTitle} - {portName} @ {baudRate.Value}"
            : AppTitle;
    }
}
