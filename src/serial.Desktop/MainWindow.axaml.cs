using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using serial.Core;

namespace serial.Desktop;

public partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;
    private LocalSettings _settings = new();

    public ObservableCollection<SessionControl> Tabs { get; } = new();

    public event Action? NewWindowRequested;
    public event Action<bool>? TimestampsToggled;

    public SessionControl? ActiveSessionControl => MainTabControl.SelectedItem as SessionControl;

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

    public IReadOnlyList<MacroDefinition> Macros => _settings.Macros;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _settings = LocalSettings.Load();
        AppFontFamily = _settings.FontFamily;

        MainTabControl.ItemsSource = Tabs;
        MainTabControl.SelectionChanged += (_, _) =>
        {
            var active = ActiveSessionControl;
            if (active != null)
            {
                TimestampsToggled?.Invoke(active.TimestampsEnabled);
            }
        };

        AddTab();

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
            else if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                e.Handled = true;
                AddTab();
            }
            else if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                e.Handled = true;
                NewWindowRequested?.Invoke();
            }
        };
    }

    public void AddTab()
    {
        var newTab = new SessionControl { Header = "Session " + (Tabs.Count + 1) };
        Tabs.Add(newTab);
        MainTabControl.SelectedItem = newTab;
    }

    public void CloseTab(object parameter)
    {
        if (parameter is SessionControl session)
        {
            session.Dispose();
            Tabs.Remove(session);

            if (Tabs.Count == 0)
            {
                Close();
            }
        }
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
                    : MacroTypes.Serial
            })
            .ToList();

        LocalSettings.Save(_settings);

        // Update macros in all open tabs
        foreach (var session in Tabs)
        {
            session.UpdateMacros(_settings.Macros);
        }
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
        _settingsWindow.Show(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _settingsWindow?.Close();
        foreach (var session in Tabs.ToList())
        {
            CloseTab(session);
        }
        base.OnClosed(e);
    }

    public async Task ShowFindWindowAsync()
    {
        var active = ActiveSessionControl;
        if (active != null)
        {
            await active.ShowFindWindowAsync();
        }
    }

    public async Task SaveLogAsync()
    {
        var active = ActiveSessionControl;
        if (active != null)
        {
            await active.SaveLogAsync();
        }
    }

    public bool ToggleTimestamps()
    {
        var active = ActiveSessionControl;
        if (active != null)
        {
            bool enabled = active.ToggleTimestamps();
            TimestampsToggled?.Invoke(enabled);
            return enabled;
        }
        return false;
    }
}
