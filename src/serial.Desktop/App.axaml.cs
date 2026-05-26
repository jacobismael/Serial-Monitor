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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        BuildAppMenu();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow mainWindow = new();
            desktop.MainWindow = mainWindow;
            BuildWindowMenu(mainWindow);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void BuildAppMenu()
    {
        NativeMenuItem aboutItem = new()
        {
            Header = "About Serial Monitor..."
        };
        aboutItem.Click += About_OnClick;
        NativeMenu appMenu = new()
        {
            Items = { aboutItem }
        };
        NativeMenu.SetMenu(this, appMenu);
    }

    private static void BuildWindowMenu(MainWindow mainWindow)
    {
        NativeMenuItem fileItem = new()
        {
            Header = "File"
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
        NativeMenuItem toggleTimeItem = new()
        {
            Header = "Show Timestamps",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = false
        };
        saveLogItem.Click += async (_, _) =>
        {
            await mainWindow.SaveLogAsync();
        };
        toggleTimeItem.Click += (_, _) =>
        {
            bool enabled = mainWindow.ToggleTimestamps();
            toggleTimeItem.IsChecked = enabled;
        };
        NativeMenu fileMenu = new()
        {
            Items = { saveLogItem }
        };
        NativeMenu viewMenu = new()
        {
            Items = { toggleTimeItem }
        };
        fileItem.Menu = fileMenu;
        viewItem.Menu = viewMenu;
        NativeMenu menuBar = new()
        {
            Items = {
                fileItem,
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
}
