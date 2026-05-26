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
        BuildWindowMenu(mainWindow);
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
            Header = "About Serial Monitor..."
        };
        settingsItem.Click += Settings_OnClick;
        aboutItem.Click += About_OnClick;
        NativeMenu appMenu = new()
        {
            Items = { settingsItem, aboutItem }
        };
        NativeMenu.SetMenu(this, appMenu);
    }

    private void BuildWindowMenu(MainWindow mainWindow)
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
        NativeMenuItem newWindowItem = new()
        {
            Header = "New Window",
            Gesture = KeyGesture.Parse("Meta+N")
        };
        NativeMenuItem toggleTimeItem = new()
        {
            Header = "Show Timestamps",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = false,
            Gesture = KeyGesture.Parse("Meta+T")
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
        mainWindow.TimestampsToggled += enabled =>
        {
            toggleTimeItem.IsChecked = enabled;
        };
        NativeMenu fileMenu = new()
        {
            Items = { newWindowItem, saveLogItem }
        };
        NativeMenu editMenu = new()
        {
            Items = { findItem }
        };
        NativeMenu viewMenu = new()
        {
            Items = { toggleTimeItem }
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
        NativeMenu.SetMenu(mainWindow, menuBar);
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
        _aboutWindow.Show();
    }

    private void Settings_OnClick(object? sender, EventArgs e)
    {
        _mainWindow?.ShowSettingsWindow();
    }
}
