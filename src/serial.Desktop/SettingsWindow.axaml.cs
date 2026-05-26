using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace serial.Desktop;

public partial class SettingsWindow : Window
{
    private readonly MainWindow? _mainWindow;
    private TextBox[] _macroNameTextBoxes = [];
    private TextBox[] _macroCommandTextBoxes = [];
    private ComboBox[] _macroTypeComboBoxes = [];

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
        _macroNameTextBoxes =
        [
            Macro1NameTextBox,
            Macro2NameTextBox,
            Macro3NameTextBox,
            Macro4NameTextBox,
            Macro5NameTextBox
        ];
        _macroCommandTextBoxes =
        [
            Macro1CommandTextBox,
            Macro2CommandTextBox,
            Macro3CommandTextBox,
            Macro4CommandTextBox,
            Macro5CommandTextBox
        ];
        _macroTypeComboBoxes =
        [
            Macro1TypeComboBox,
            Macro2TypeComboBox,
            Macro3TypeComboBox,
            Macro4TypeComboBox,
            Macro5TypeComboBox
        ];

        foreach (ComboBox comboBox in _macroTypeComboBoxes)
        {
            comboBox.ItemsSource = new[] { MacroTypes.Serial, MacroTypes.Shell };
            comboBox.SelectedItem = MacroTypes.Serial;
        }

        for (int i = 0; i < _macroNameTextBoxes.Length && i < _mainWindow.Macros.Count; i++)
        {
            _macroNameTextBoxes[i].Text = _mainWindow.Macros[i].Name;
            _macroCommandTextBoxes[i].Text = _mainWindow.Macros[i].Command;
            _macroTypeComboBoxes[i].SelectedItem = NormalizeMacroType(_mainWindow.Macros[i].Type);
        }

        ApplyButton.Click += (_, _) => ApplySettings();
        CancelButton.Click += (_, _) => Close();
        BrowseLogSaveLocationButton.Click += async (_, _) => await BrowseLogSaveLocationAsync();

        FontFamilyTextBox.KeyDown += (_, e) =>
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

        LogSaveLocationTextBox.KeyDown += (_, e) =>
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

        foreach (TextBox textBox in _macroNameTextBoxes.Concat(_macroCommandTextBoxes))
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
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        FontFamilyTextBox.Focus();
        FontFamilyTextBox.SelectAll();
    }

    private void ApplySettings()
    {
        if (_mainWindow != null)
        {
            _mainWindow.AppFontFamily = FontFamilyTextBox.Text ?? "";
            _mainWindow.DefaultLogSaveLocation = LogSaveLocationTextBox.Text ?? "";
            _mainWindow.UpdateMacros(GetMacros());
        }

        Close();
    }

    private IReadOnlyList<MacroDefinition> GetMacros()
    {
        List<MacroDefinition> macros = [];

        for (int i = 0; i < _macroNameTextBoxes.Length; i++)
        {
            macros.Add(new MacroDefinition
            {
                Name = _macroNameTextBoxes[i].Text ?? "",
                Command = _macroCommandTextBoxes[i].Text ?? "",
                Type = NormalizeMacroType(_macroTypeComboBoxes[i].SelectedItem as string)
            });
        }

        return macros;
    }

    private static string NormalizeMacroType(string? macroType)
    {
        return string.Equals(macroType, MacroTypes.Shell, System.StringComparison.OrdinalIgnoreCase)
            ? MacroTypes.Shell
            : MacroTypes.Serial;
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
}
