using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Input;

namespace serial.Desktop;

public partial class App : Application
{
    private AboutWindow? _aboutWindow;
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        BuildAppMenu();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateMainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateMainWindow()
    {
        MainWindow mainWindow = new();
        _mainWindow = mainWindow;
        mainWindow.NewWindowRequested += () => CreateMainWindow().Show();
        mainWindow.Activated += (_, _) => _mainWindow = mainWindow;
        AttachWindowMenu(mainWindow, mainWindow);
        return mainWindow;
    }

    private void BuildAppMenu()
    {
        NativeMenuItem settingsItem = new()
        {
            Header = "Settings...",
            Gesture = KeyGesture.Parse("Meta+,")
        };
        NativeMenuItem aboutItem = new()
        {
            Header = "About Logicom..."
        };
        settingsItem.Click += Settings_OnClick;
        aboutItem.Click += About_OnClick;
        NativeMenu appMenu = new()
        {
            Items = { aboutItem, settingsItem }
        };
        NativeMenu.SetMenu(this, appMenu);
    }

    public void AttachWindowMenu(Window window, MainWindow mainWindow)
    {
        BuildWindowMenu(window, mainWindow);
    }

    private void BuildWindowMenu(Window window, MainWindow mainWindow)
    {
        NativeMenuItem fileItem = new()
        {
            Header = "File"
        };
        NativeMenuItem editItem = new()
        {
            Header = "Edit"
        };
        NativeMenuItem viewItem = new()
        {
            Header = "View"
        };
        NativeMenuItem saveLogItem = new()
        {
            Header = "Save Log...",
            Gesture = KeyGesture.Parse("Meta+S")
        };
        NativeMenuItem openCaptureItem = new()
        {
            Header = "Open Capture..."
        };
        NativeMenuItem saveCaptureItem = new()
        {
            Header = "Save Capture..."
        };
        NativeMenuItem exportCaptureCsvItem = new()
        {
            Header = "Export CSV..."
        };
        NativeMenuItem exportCaptureVcdItem = new()
        {
            Header = "Export VCD..."
        };
        NativeMenuItem exportDecodedCsvItem = new()
        {
            Header = "Export Decoded CSV..."
        };
        NativeMenuItem copySerialMonitorItem = new()
        {
            Header = "Copy Serial Monitor",
            Gesture = KeyGesture.Parse("Meta+Shift+C")
        };
        NativeMenuItem newWindowItem = new()
        {
            Header = "New Window",
            Gesture = KeyGesture.Parse("Meta+N")
        };
        NativeMenuItem toggleTimeItem = new()
        {
            Header = "Show Timestamps",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = mainWindow.TimestampsEnabled,
            Gesture = KeyGesture.Parse("Ctrl+T")
        };
        NativeMenuItem showStatusPanelItem = new()
        {
            Header = "Show Status Panel"
        };
        NativeMenuItem showPlotterStatusPanelItem = new()
        {
            Header = "Show Plotter Status Panel"
        };
        NativeMenuItem showLogicProbeStatusPanelItem = new()
        {
            Header = "Show Logic Probe Status Panel"
        };
        NativeMenuItem showProtocolAnalyzerItem = new()
        {
            Header = "Show Protocol Analyzer"
        };
        NativeMenuItem findItem = new()
        {
            Header = "Find...",
            Gesture = KeyGesture.Parse("Meta+F")
        };
        saveLogItem.Click += async (_, _) =>
        {
            await mainWindow.SaveLogAsync();
        };
        openCaptureItem.Click += async (_, _) =>
        {
            await mainWindow.OpenLogicCaptureAsync();
        };
        saveCaptureItem.Click += async (_, _) =>
        {
            await mainWindow.SaveLogicCaptureAsync();
        };
        exportCaptureCsvItem.Click += async (_, _) =>
        {
            await mainWindow.ExportLogicCaptureCsvAsync();
        };
        exportCaptureVcdItem.Click += async (_, _) =>
        {
            await mainWindow.ExportLogicCaptureVcdAsync();
        };
        exportDecodedCsvItem.Click += async (_, _) =>
        {
            await mainWindow.ExportLogicDecodedCsvAsync();
        };
        copySerialMonitorItem.Click += async (_, _) =>
        {
            await mainWindow.CopySerialMonitorAsync();
        };
        newWindowItem.Click += (_, _) =>
        {
            CreateMainWindow().Show();
        };
        findItem.Click += async (_, _) =>
        {
            await mainWindow.ShowFindWindowAsync();
        };
        toggleTimeItem.Click += (_, _) =>
        {
            bool enabled = mainWindow.ToggleTimestamps();
            toggleTimeItem.IsChecked = enabled;
        };
        showStatusPanelItem.Click += (_, _) =>
        {
            mainWindow.ShowStatusPanel();
        };
        showPlotterStatusPanelItem.Click += (_, _) =>
        {
            mainWindow.ShowPlotterStatusPanel();
        };
        showLogicProbeStatusPanelItem.Click += (_, _) =>
        {
            mainWindow.ShowLogicAnalyzerProbeStatusPanel();
        };
        showProtocolAnalyzerItem.Click += (_, _) =>
        {
            mainWindow.ShowProtocolAnalyzer();
        };
        mainWindow.TimestampsToggled += enabled =>
        {
            toggleTimeItem.IsChecked = enabled;
        };
        NativeMenu fileMenu = new()
        {
            Items =
            {
                newWindowItem,
                saveLogItem,
                openCaptureItem,
                saveCaptureItem,
                exportCaptureCsvItem,
                exportCaptureVcdItem,
                exportDecodedCsvItem
            }
        };
        NativeMenu editMenu = new()
        {
            Items = { findItem, copySerialMonitorItem }
        };
        NativeMenu viewMenu = new()
        {
            Items =
            {
                toggleTimeItem,
                showStatusPanelItem,
                showPlotterStatusPanelItem,
                showLogicProbeStatusPanelItem,
                showProtocolAnalyzerItem
            }
        };
        fileItem.Menu = fileMenu;
        editItem.Menu = editMenu;
        viewItem.Menu = viewMenu;
        NativeMenu menuBar = new()
        {
            Items = {
                fileItem,
                editItem,
                viewItem
            }
        };
        NativeMenu.SetMenu(window, menuBar);
    }

    private void About_OnClick(object? sender, EventArgs e)
    {
        if (_aboutWindow is { IsVisible: true })
        {
            _aboutWindow.Activate();
            return;
        }
        _aboutWindow = new AboutWindow();
        _aboutWindow.Closed += (_, _) =>
        {
            _aboutWindow = null;
        };
        if (_mainWindow != null)
        {
            AttachWindowMenu(_aboutWindow, _mainWindow);
        }
        _aboutWindow.Show();
    }

    private void Settings_OnClick(object? sender, EventArgs e)
    {
        _mainWindow?.ShowSettingsWindow();
    }
}
