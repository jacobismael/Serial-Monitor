using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Shapes;
using serial.Core;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Input.Platform;

namespace serial.Desktop;

public partial class SessionControl : UserControl, IDisposable
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<SessionControl, string>(nameof(Header), "New Session");

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    private const string TimestampColumnPadding = "           ";

    private SerialMonitor? _monitor;
    private Window? _findWindow;
    private Window? _signalViewerWindow;
    private Window? _statusPanelWindow;
    private Window? _serialPlotterWindow;
    private Canvas? _serialPlotterCanvas;
    private TextBlock? _serialPlotterStatusTextBlock;
    private TextBox? _findTextBox;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly List<LogEntry> _logEntries = [];
    private readonly List<MacroDefinition> _macroAutocompleteMatches = [];
    private readonly List<double> _serialPlotterSamples = [];
    private bool _timestampsEnabled;
    private bool _isDisposing;
    private bool _suppressMacroAutocomplete;
    private double _dockedSignalViewerWidth = 360;
    private int _waveformProbeCount;
    private string _searchText = "";
    private int _matchCount;
    private int _activeMatchIndex = -1;
    private int _renderedMatchIndex;

    private string? _portName;
    private int? _baudRate;
    private DateTime? _connectedAt;
    private DateTime? _lastMcuResetAt;

    private LocalSettings _settings = new();

    public event Action<bool>? TimestampsToggled;
    public event Action<bool>? SignalViewerDetachedChanged;
    public event Action<bool>? StatusPanelDetachedChanged;
    public event Action<bool>? SerialPlotterToggled;
    public event Action<Window>? UtilityWindowCreated;
    public event Action<string?, int?>? ConnectionChanged;
    public event Action? StatusSettingsRequested;

    public bool TimestampsEnabled => _timestampsEnabled;
    public bool IsSignalViewerVisible => SignalViewerPanel.IsVisible;
    public bool IsSignalViewerDetached => _signalViewerWindow != null;
    public bool IsStatusPanelDetached => _statusPanelWindow != null;
    public bool IsSerialPlotterVisible => _serialPlotterWindow?.IsVisible == true;

    public IReadOnlyList<MacroDefinition> Macros => _settings.Macros;

    public SessionControl()
    {
        InitializeComponent();
        _settings = LocalSettings.Load();
        _uptimeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uptimeTimer.Tick += (_, _) => UpdateStatusPanel();

        BaudComboBox.ItemsSource = new int[]
        {
            9600,
            19200,
            38400,
            57600,
            115200,
            230400,
            460800,
            921600
        };

        BaudComboBox.SelectedItem = 9600;
        LineEndingComboBox.ItemsSource = Enum.GetValues<SerialLineEnding>();
        LineEndingComboBox.SelectedItem = SerialLineEnding.None;
        LineEndingComboBox.SelectionChanged += (_, _) => UpdateStatusPanel();
        PortComboBox.SelectionChanged += (_, _) => UpdateStatusPanel();
        BaudComboBox.SelectionChanged += (_, _) => UpdateStatusPanel();

        RefreshButton.Click += (_, _) => RefreshPorts();
        ConnectButton.Click += (_, _) => ToggleConnection();
        SendButton.Click += (_, _) => SendCommand();
        ClearButton.Click += async (_, _) => await ConfirmAndClearOutput();
        ConfigureSignalViewerContextMenu();
        ConfigureStatusPanelContextMenu();
        ConfigureSerialMonitorContextMenu();
        WaveformScrollViewer.SizeChanged += (_, _) => UpdateWaveformLayoutHeight();
        CommandTextBox.TextChanged += (_, _) => UpdateMacroAutocomplete();
        MacroAutocompleteListBox.DoubleTapped += (_, _) => ApplyMacroAutocomplete();

        CommandTextBox.KeyDown += (_, e) =>
        {
            if (HandleMacroAutocompleteKeyDown(e))
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                SendCommand();
            }
        };

        SetConnectionUiState(isConnected: false);
        UpdateWaveformProbes(_settings.WaveformProbes);
        SignalViewerPanel.IsVisible = false;
        SetDockedSignalViewerColumn(isVisible: false);
        RenderMacroButtons();

        RefreshPorts();
    }

    private void ToggleConnection()
    {
        if (_monitor?.IsOpen == true)
        {
            Disconnect();
            return;
        }

        Connect();
    }

    private void Connect()
    {
        try
        {
            _portName = PortComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(_portName))
            {
                AppendOutput("No port selected.\n");
                return;
            }

            if (BaudComboBox.SelectedItem is not int baudRate)
            {
                AppendOutput("No baud rate selected.\n");
                return;
            }

            _baudRate = baudRate;
            _monitor = new SerialMonitor(_portName, baudRate, CreateSerialPortSettings());
            _monitor.DataReceived += data =>
            {
                DateTime receivedAt = DateTime.Now;
                Dispatcher.UIThread.Post(() =>
                {
                    AppendOutput(data + "\n", timestamp: receivedAt);
                    ProcessSerialPlotterData(data);
                });
            };
            _monitor.ErrorReceived += ex =>
            {
                DateTime receivedAt = DateTime.Now;
                Dispatcher.UIThread.Post(() =>
                {
                    AppendErrorOutput("Serial error", ex.Message, receivedAt);
                });
            };
            _monitor.Open();

            _connectedAt = DateTime.Now;
            _uptimeTimer.Start();
            AppendStatusOutput("Connected", $"to {_portName} at {baudRate} baud.", Brushes.LimeGreen);
            SetConnectionUiState(isConnected: true);
            ConnectionChanged?.Invoke(_portName, baudRate);
        }
        catch (Exception ex)
        {
            AppendErrorOutput("Connect failed", ex.Message);
        }
    }

    private void SendCommand()
    {
        string? command = CommandTextBox.Text;
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        CommandTextBox.Text = "";

        if (TryExecuteMacroCommand(command))
        {
            return;
        }

        if (_monitor?.IsOpen != true)
        {
            AppendOutput("Not connected.\n");
            return;
        }

        SendCommandText(command);
    }

    private bool TryExecuteMacroCommand(string command)
    {
        if (!command.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        string macroName = command[1..].Trim();
        MacroDefinition? macro = _settings.Macros.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, macroName, StringComparison.OrdinalIgnoreCase));

        if (macro == null)
        {
            AppendErrorOutput("No macro exists", string.IsNullOrWhiteSpace(macroName) ? command : "/" + macroName);
            return true;
        }

        ExecuteMacro(macro);
        return true;
    }

    private void UpdateMacroAutocomplete()
    {
        if (_suppressMacroAutocomplete)
        {
            return;
        }

        string text = CommandTextBox.Text ?? "";
        if (!text.StartsWith("/", StringComparison.Ordinal))
        {
            HideMacroAutocomplete();
            return;
        }

        string prefix = text[1..];
        if (prefix.Contains(' ', StringComparison.Ordinal))
        {
            HideMacroAutocomplete();
            return;
        }

        _macroAutocompleteMatches.Clear();
        _macroAutocompleteMatches.AddRange(_settings.Macros
            .Where(macro => !string.IsNullOrWhiteSpace(macro.Name))
            .Where(macro => prefix.Length == 0
                || macro.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(macro => macro.Name, StringComparer.OrdinalIgnoreCase));

        if (_macroAutocompleteMatches.Count == 0)
        {
            HideMacroAutocomplete();
            return;
        }

        MacroAutocompleteListBox.ItemsSource = _macroAutocompleteMatches
            .Select(macro => $"/{macro.Name}    {macro.Command}")
            .ToList();
        MacroAutocompleteListBox.SelectedIndex = 0;
        MacroAutocompletePanel.IsVisible = true;
    }

    private bool HandleMacroAutocompleteKeyDown(KeyEventArgs e)
    {
        if (!MacroAutocompletePanel.IsVisible)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Up:
                e.Handled = true;
                MacroAutocompleteListBox.SelectedIndex = Math.Max(0, MacroAutocompleteListBox.SelectedIndex - 1);
                return true;
            case Key.Down:
                e.Handled = true;
                MacroAutocompleteListBox.SelectedIndex = Math.Min(
                    _macroAutocompleteMatches.Count - 1,
                    MacroAutocompleteListBox.SelectedIndex + 1);
                return true;
            case Key.Tab:
                e.Handled = true;
                ApplyMacroAutocomplete();
                return true;
            case Key.Escape:
                e.Handled = true;
                HideMacroAutocomplete();
                return true;
            case Key.Enter:
                e.Handled = true;
                ApplyMacroAutocomplete();
                return true;
            default:
                return false;
        }
    }

    private void ApplyMacroAutocomplete()
    {
        int selectedIndex = MacroAutocompleteListBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _macroAutocompleteMatches.Count)
        {
            return;
        }

        string commandText = "/" + _macroAutocompleteMatches[selectedIndex].Name;
        if (string.Equals(CommandTextBox.Text, commandText, StringComparison.Ordinal))
        {
            HideMacroAutocomplete();
            CommandTextBox.Focus();
            return;
        }

        _suppressMacroAutocomplete = true;
        CommandTextBox.Text = commandText;
        CommandTextBox.CaretIndex = commandText.Length;
        _suppressMacroAutocomplete = false;
        HideMacroAutocomplete();
        CommandTextBox.Focus();
    }

    private void HideMacroAutocomplete()
    {
        MacroAutocompletePanel.IsVisible = false;
        MacroAutocompleteListBox.ItemsSource = null;
        _macroAutocompleteMatches.Clear();
    }

    private async void ExecuteMacro(MacroDefinition macro)
    {
        if (string.IsNullOrWhiteSpace(macro.Command))
        {
            return;
        }

        if (string.Equals(macro.Type, MacroTypes.Shell, StringComparison.OrdinalIgnoreCase))
        {
            await RunShellCommandAsync(macro);
            return;
        }

        SendCommandText(macro.Command, GetMacroLineEnding(macro.Ending));
    }

    private async Task RunShellCommandAsync(MacroDefinition macro)
    {
        string label = string.IsNullOrWhiteSpace(macro.Name) ? "Command" : macro.Name;
        AppendStatusOutput("$ ", $"{label}: {macro.Command}", Brushes.DeepSkyBlue);

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "/bin/zsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(macro.Command);

            using Process process = new()
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                AppendErrorOutput("Command failed", "Process did not start.");
                return;
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string output = await outputTask;
            string error = await errorTask;

            if (!string.IsNullOrEmpty(output))
            {
                AppendProcessOutput(output);
            }

            if (!string.IsNullOrEmpty(error))
            {
                AppendProcessOutput(error, Brushes.Red);
            }

            if (process.ExitCode == 0)
            {
                AppendStatusOutput("Finished", $"{label} exited with code 0.", Brushes.LimeGreen);
            }
            else
            {
                AppendErrorOutput("Command failed", $"{label} exited with code {process.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            AppendErrorOutput("Command failed", ex.Message);
        }
    }

    private void AppendProcessOutput(string output, IBrush? defaultForeground = null)
    {
        AppendOutputSegments(ParseAnsiOutput(output, defaultForeground));
    }

    private static IReadOnlyList<LogSegment> ParseAnsiOutput(string output, IBrush? defaultForeground)
    {
        List<LogSegment> segments = [];
        IBrush? foreground = defaultForeground;
        bool bold = false;
        int textStartIndex = 0;

        foreach (Match match in AnsiSgrRegex().Matches(output))
        {
            AddAnsiTextSegment(
                segments,
                output[textStartIndex..match.Index],
                foreground,
                bold);

            ApplyAnsiSgr(match.Groups[1].Value, defaultForeground, ref foreground, ref bold);
            textStartIndex = match.Index + match.Length;
        }

        AddAnsiTextSegment(
            segments,
            output[textStartIndex..],
            foreground,
            bold);

        return segments;
    }

    private static void AddAnsiTextSegment(
        List<LogSegment> segments,
        string text,
        IBrush? foreground,
        bool bold)
    {
        if (text.Length == 0)
        {
            return;
        }

        string cleanText = AnsiEscapeRegex().Replace(text, "");
        if (cleanText.Length > 0)
        {
            segments.Add(new LogSegment(cleanText, foreground, bold));
        }
    }

    private static void ApplyAnsiSgr(
        string sgr,
        IBrush? defaultForeground,
        ref IBrush? foreground,
        ref bool bold)
    {
        string[] codes = string.IsNullOrWhiteSpace(sgr) ? ["0"] : sgr.Split(';');

        foreach (string codeText in codes)
        {
            if (!int.TryParse(codeText, out int code))
            {
                continue;
            }

            switch (code)
            {
                case 0:
                    foreground = defaultForeground;
                    bold = false;
                    break;
                case 1:
                    bold = true;
                    break;
                case 22:
                    bold = false;
                    break;
                case 30:
                    foreground = Brushes.Black;
                    break;
                case 31:
                    foreground = Brushes.Red;
                    break;
                case 32:
                    foreground = Brushes.LimeGreen;
                    break;
                case 33:
                    foreground = Brushes.Yellow;
                    break;
                case 34:
                    foreground = Brushes.DodgerBlue;
                    break;
                case 35:
                    foreground = Brushes.Magenta;
                    break;
                case 36:
                    foreground = Brushes.Cyan;
                    break;
                case 37:
                    foreground = Brushes.White;
                    break;
                case 39:
                    foreground = defaultForeground;
                    break;
                case 90:
                    foreground = Brushes.Gray;
                    break;
                case 91:
                    foreground = Brushes.OrangeRed;
                    break;
                case 92:
                    foreground = Brushes.LimeGreen;
                    break;
                case 93:
                    foreground = Brushes.Yellow;
                    break;
                case 94:
                    foreground = Brushes.DeepSkyBlue;
                    break;
                case 95:
                    foreground = Brushes.Violet;
                    break;
                case 96:
                    foreground = Brushes.Cyan;
                    break;
                case 97:
                    foreground = Brushes.White;
                    break;
            }
        }
    }

    [GeneratedRegex(@"\x1B\[([0-9;]*)m")]
    private static partial Regex AnsiSgrRegex();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"^P\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ProbeAliasRegex();

    private void SendCommandText(string command, SerialLineEnding? lineEndingOverride = null)
    {
        if (_monitor?.IsOpen != true)
        {
            AppendOutput("Not connected.\n");
            return;
        }

        AppendOutput($"> {command}\n");

        try
        {
            SerialLineEnding lineEnding;
            if (lineEndingOverride.HasValue)
            {
                lineEnding = lineEndingOverride.Value;
            }
            else if (LineEndingComboBox.SelectedItem is not SerialLineEnding selectedLineEnding)
            {
                lineEnding = SerialLineEnding.None;
            }
            else
            {
                lineEnding = selectedLineEnding;
            }

            _monitor.Write(command, lineEnding);
            MaybeRecordMcuReset(command);
        }
        catch (Exception ex)
        {
            AppendErrorOutput("Send failed", ex.Message);
        }
    }

    private static SerialLineEnding? GetMacroLineEnding(string? ending)
    {
        return NormalizeMacroEnding(ending) switch
        {
            MacroEndingTypes.None => SerialLineEnding.None,
            MacroEndingTypes.LF => SerialLineEnding.LF,
            MacroEndingTypes.CR => SerialLineEnding.CR,
            MacroEndingTypes.CRLF => SerialLineEnding.CRLF,
            _ => null
        };
    }

    private static string NormalizeMacroEnding(string? ending)
    {
        return ending switch
        {
            MacroEndingTypes.None => MacroEndingTypes.None,
            MacroEndingTypes.LF => MacroEndingTypes.LF,
            MacroEndingTypes.CR => MacroEndingTypes.CR,
            MacroEndingTypes.CRLF => MacroEndingTypes.CRLF,
            _ => MacroEndingTypes.Current
        };
    }

    private void MaybeRecordMcuReset(string command)
    {
        if (command.Contains("reset", StringComparison.OrdinalIgnoreCase))
        {
            _lastMcuResetAt = DateTime.Now;
            UpdateStatusPanel();
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
                    : MacroTypes.Serial,
                Ending = NormalizeMacroEnding(macro.Ending),
                ShowAsButton = macro.ShowAsButton
            })
            .ToList();

        LocalSettings.Save(_settings);
        RenderMacroButtons();
    }

    private void RenderMacroButtons()
    {
        MacroButtonsPanel.Children.Clear();

        foreach (MacroDefinition macro in _settings.Macros
            .Where(macro => !string.IsNullOrWhiteSpace(macro.Command))
            .Where(macro => macro.ShowAsButton))
        {
            Button button = new()
            {
                Content = string.IsNullOrWhiteSpace(macro.Name) ? macro.Command : macro.Name,
                Margin = new Avalonia.Thickness(0, 0, 8, 8),
                MinWidth = 80,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            button.Click += (_, _) => ExecuteMacro(macro);
            MacroButtonsPanel.Children.Add(button);
        }
    }

    private void Disconnect()
    {
        _monitor?.Dispose();
        _monitor = null;
        _uptimeTimer.Stop();
        _connectedAt = null;

        AppendStatusOutput("Disconnected", $"from {_portName}.", Brushes.Cyan);
        SetConnectionUiState(isConnected: false);
        ConnectionChanged?.Invoke(null, null);
    }

    private void SetConnectionUiState(bool isConnected)
    {
        ConnectButton.Content = isConnected ? "Disconnect" : "Connect";
        SendButton.IsEnabled = isConnected;
        PortComboBox.IsEnabled = !isConnected;
        BaudComboBox.IsEnabled = !isConnected;
        RefreshButton.IsEnabled = !isConnected;
        LineEndingComboBox.IsEnabled = true;
        UpdateStatusPanel();
    }

    private void RefreshPorts()
    {
        string[] ports = SerialMonitor.GetAvailablePorts();
        PortComboBox.ItemsSource = ports;
        if (ports.Length > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }
        UpdateStatusPanel();
    }

    public bool ToggleSignalViewer()
    {
        SignalViewerPanel.IsVisible = !SignalViewerPanel.IsVisible;
        if (IsSignalViewerDetached)
        {
            if (SignalViewerPanel.IsVisible)
            {
                _signalViewerWindow?.Show();
                _signalViewerWindow?.Activate();
            }
            else
            {
                _signalViewerWindow?.Hide();
            }
        }
        else
        {
            SetDockedSignalViewerColumn(SignalViewerPanel.IsVisible);
        }

        return SignalViewerPanel.IsVisible;
    }

    public bool ToggleSignalViewerDetached()
    {
        if (IsSignalViewerDetached)
        {
            AttachSignalViewer();
            return false;
        }

        DetachSignalViewer();
        return true;
    }

    private void DetachSignalViewer()
    {
        if (IsSignalViewerDetached)
        {
            _signalViewerWindow?.Activate();
            return;
        }

        SignalViewerPanel.IsVisible = true;
        MainContentGrid.Children.Remove(SignalViewerPanel);
        SignalViewerPanel.Margin = new Thickness(0);
        SetDockedSignalViewerColumn(isVisible: false);

        Window window = new()
        {
            Title = "Logic Analyzer",
            Width = 560,
            Height = 520,
            MinWidth = 360,
            MinHeight = 360,
            Content = SignalViewerPanel
        };

        _signalViewerWindow = window;
        UtilityWindowCreated?.Invoke(window);
        window.Closed += (_, _) =>
        {
            if (_isDisposing)
            {
                return;
            }

            AttachSignalViewer();
        };

        if (Window.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        SignalViewerDetachedChanged?.Invoke(true);
    }

    private void AttachSignalViewer()
    {
        Window? window = _signalViewerWindow;
        if (window == null)
        {
            return;
        }

        _signalViewerWindow = null;
        window.Content = null;

        if (!MainContentGrid.Children.Contains(SignalViewerPanel))
        {
            Grid.SetColumn(SignalViewerPanel, 2);
            MainContentGrid.Children.Add(SignalViewerPanel);
        }

        SignalViewerPanel.IsVisible = true;
        SignalViewerPanel.Margin = new Thickness(10, 0, 0, 0);
        SetDockedSignalViewerColumn(isVisible: true);

        if (window.IsVisible)
        {
            window.Close();
        }

        SignalViewerDetachedChanged?.Invoke(false);
    }

    private void DetachStatusPanel()
    {
        if (_statusPanelWindow != null)
        {
            _statusPanelWindow.Show();
            _statusPanelWindow.Activate();
            return;
        }

        RemoveStatusPanelFromParent();
        StatusPanel.Margin = new Thickness(0);
        StatusPanel.IsVisible = true;

        Window window = new()
        {
            Title = "Logicom Status",
            Width = 430,
            Height = 340,
            MinWidth = 360,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = StatusPanel
        };

        _statusPanelWindow = window;
        UtilityWindowCreated?.Invoke(window);
        window.Closed += (_, _) =>
        {
            if (_isDisposing)
            {
                return;
            }

            if (_statusPanelWindow == window)
            {
                AttachStatusPanel(isVisible: false);
            }
        };

        window.Show();
        window.Activate();

        StatusPanelDetachedChanged?.Invoke(true);
    }

    private void AttachStatusPanel(bool isVisible = true)
    {
        Window? window = _statusPanelWindow;
        _statusPanelWindow = null;

        if (window != null)
        {
            window.Content = null;
        }

        if (!FooterPanel.Children.Contains(StatusPanel))
        {
            FooterPanel.Children.Insert(0, StatusPanel);
        }

        StatusPanel.Margin = new Thickness(0);
        StatusPanel.IsVisible = isVisible;

        if (window?.IsVisible == true)
        {
            window.Close();
        }

        StatusPanelDetachedChanged?.Invoke(false);
    }

    public bool ToggleStatusPanelDetached()
    {
        if (_statusPanelWindow != null)
        {
            AttachStatusPanel();
            return false;
        }

        DetachStatusPanel();
        return true;
    }

    private void CloseStatusPanel()
    {
        if (_statusPanelWindow != null)
        {
            _statusPanelWindow.Close();
            return;
        }

        StatusPanel.IsVisible = false;
    }

    private void RemoveStatusPanelFromParent()
    {
        if (StatusPanel.Parent is Panel parent)
        {
            parent.Children.Remove(StatusPanel);
        }
    }

    private void SetDockedSignalViewerColumn(bool isVisible)
    {
        if (!isVisible && MainContentGrid.ColumnDefinitions[2].ActualWidth > 0)
        {
            _dockedSignalViewerWidth = MainContentGrid.ColumnDefinitions[2].ActualWidth;
        }

        SignalViewerSplitter.IsVisible = isVisible;
        MainContentGrid.ColumnDefinitions[1].Width = isVisible
            ? new GridLength(6)
            : new GridLength(0);
        MainContentGrid.ColumnDefinitions[2].Width = isVisible
            ? new GridLength(_dockedSignalViewerWidth)
            : new GridLength(0);
    }

    private void ConfigureSignalViewerContextMenu()
    {
        MenuItem closeItem = new()
        {
            Header = "Close Logic Analyzer"
        };
        MenuItem detachItem = new()
        {
            Header = "Detach / Attach Logic Analyzer"
        };
        MenuItem showStatusItem = new()
        {
            Header = "Show Status Panel"
        };

        closeItem.Click += (_, _) =>
        {
            if (SignalViewerPanel.IsVisible)
            {
                ToggleSignalViewer();
            }
        };
        detachItem.Click += (_, _) => ToggleSignalViewerDetached();
        showStatusItem.Click += (_, _) => ShowStatusPanel();

        SignalViewerPanel.ContextMenu = new ContextMenu
        {
            Items =
            {
                closeItem,
                detachItem,
                showStatusItem
            }
        };
    }

    public void ShowStatusPanel()
    {
        if (_statusPanelWindow != null)
        {
            _statusPanelWindow.Show();
            _statusPanelWindow.Activate();
            return;
        }

        DetachStatusPanel();
    }

    private void ConfigureStatusPanelContextMenu()
    {
        MenuItem closeItem = new()
        {
            Header = "Close Status Panel"
        };
        MenuItem detachItem = new()
        {
            Header = "Detach / Attach Status Panel"
        };
        MenuItem customizeItem = new()
        {
            Header = "Customize Status Settings..."
        };

        closeItem.Click += (_, _) => Dispatcher.UIThread.Post(CloseStatusPanel);
        detachItem.Click += (_, _) => Dispatcher.UIThread.Post(() => ToggleStatusPanelDetached());
        customizeItem.Click += (_, _) => StatusSettingsRequested?.Invoke();

        StatusPanel.ContextMenu = new ContextMenu
        {
            Items =
            {
                closeItem,
                detachItem,
                customizeItem
            }
        };
    }

    private void ConfigureSerialMonitorContextMenu()
    {
        MenuItem copyItem = new()
        {
            Header = "Copy Serial Monitor"
        };

        copyItem.Click += async (_, _) => await CopySerialMonitorAsync();

        OutputTextBlock.ContextMenu = new ContextMenu
        {
            Items =
            {
                copyItem
            }
        };
    }

    public void UpdateStatusPanelSettings(StatusPanelSettings settings)
    {
        settings.Normalize();
        _settings.StatusPanel = settings;
        LocalSettings.Save(_settings);
        StatusDataBitsTextBlock.Text = settings.DataBits;
        StatusParityTextBlock.Text = settings.Parity;
        StatusStopBitsTextBlock.Text = settings.StopBits;
        StatusFlowControlTextBlock.Text = settings.FlowControl;
    }

    public void UpdateSerialPlotterSettings(SerialPlotterSettings settings)
    {
        settings.Normalize();
        _settings.SerialPlotter = settings;
        LocalSettings.Save(_settings);
        TrimSerialPlotterSamples();
        RedrawSerialPlotter();
    }

    private SerialPortSettings CreateSerialPortSettings()
    {
        StatusPanelSettings settings = _settings.StatusPanel;
        settings.Normalize();

        return new SerialPortSettings(
            ParseDataBits(settings.DataBits),
            ParseParity(settings.Parity),
            ParseStopBits(settings.StopBits),
            ParseHandshake(settings.FlowControl));
    }

    private static int ParseDataBits(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dataBits)
            && dataBits is >= 5 and <= 8
            ? dataBits
            : 8;
    }

    private static Parity ParseParity(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "even" => Parity.Even,
            "odd" => Parity.Odd,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => Parity.None
        };
    }

    private static StopBits ParseStopBits(string value)
    {
        return value.Trim() switch
        {
            "1.5" => StopBits.OnePointFive,
            "2" => StopBits.Two,
            _ => StopBits.One
        };
    }

    private static Handshake ParseHandshake(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "rts/cts" => Handshake.RequestToSend,
            "xon/xoff" => Handshake.XOnXOff,
            _ => Handshake.None
        };
    }

    public void UpdateWaveformProbes(IEnumerable<WaveformProbeDefinition> probes)
    {
        List<WaveformProbeDefinition> normalized = LocalSettings.NormalizeWaveformProbes(probes);
        _settings.WaveformProbes = normalized;
        LocalSettings.Save(_settings);
        _waveformProbeCount = normalized.Count;
        UpdateWaveformLayoutHeight();

        WaveformRowsGrid.Children.Clear();
        WaveformRowsGrid.RowDefinitions.Clear();

        if (normalized.Count == 0)
        {
            WaveformRowsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            WaveformRowsGrid.Children.Add(new TextBlock
            {
                Text = "No probes configured.",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            });
            LogicAnalyzerHoverTextBlock.Text = "Logic level: -";
            return;
        }

        for (int i = 0; i < normalized.Count; i++)
        {
            WaveformRowsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            WaveformProbeDefinition probe = normalized[i];
            IBrush foreground = CreateProbeBrush(probe.Color, i);
            Canvas waveformCanvas = new()
            {
                Background = Brushes.Transparent,
                MinHeight = 72,
                MinWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid row = new()
            {
                Background = Brushes.Transparent,
                ColumnSpacing = 8,
                MinHeight = 84,
                VerticalAlignment = VerticalAlignment.Stretch,
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(92)),
                    new ColumnDefinition(new GridLength(1, GridUnitType.Star))
                }
            };
            string displayName = GetWaveformDisplayName(probe);
            TextBlock signalTextBlock = new()
            {
                Text = displayName,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center
            };
            int probeIndex = i;

            Grid.SetColumn(signalTextBlock, 0);
            Grid.SetColumn(waveformCanvas, 1);
            Grid.SetRow(row, i);
            row.Children.Add(signalTextBlock);
            row.Children.Add(waveformCanvas);
            WaveformRowsGrid.Children.Add(row);
            waveformCanvas.SizeChanged += (_, _) => DrawWaveform(waveformCanvas, foreground, probeIndex);
            waveformCanvas.PointerMoved += (_, e) =>
            {
                UpdateLogicAnalyzerHover(waveformCanvas, probeIndex, displayName, e.GetPosition(waveformCanvas));
            };
            row.PointerMoved += (_, e) =>
            {
                UpdateLogicAnalyzerHover(waveformCanvas, probeIndex, displayName, e.GetPosition(waveformCanvas));
            };
            row.PointerExited += (_, _) =>
            {
                LogicAnalyzerHoverTextBlock.Text = "Logic level: -";
            };
            DrawWaveform(waveformCanvas, foreground, probeIndex);
        }
    }

    private void UpdateWaveformLayoutHeight()
    {
        double viewportHeight = Math.Max(0, WaveformScrollViewer.Bounds.Height);
        double contentHeight = Math.Max(1, _waveformProbeCount) * 84;
        WaveformRowsGrid.MinHeight = Math.Max(viewportHeight, contentHeight);
    }

    private static string GetWaveformDisplayName(WaveformProbeDefinition probe)
    {
        string signal = probe.Signal.Trim();
        string hardwareProbe = probe.Probe.Trim();
        if (!string.IsNullOrWhiteSpace(signal) && !ProbeAliasRegex().IsMatch(signal))
        {
            return signal;
        }

        return string.IsNullOrWhiteSpace(hardwareProbe) ? signal : hardwareProbe;
    }

    private void UpdateLogicAnalyzerHover(Canvas canvas, int probeIndex, string signalName, Point pointerPosition)
    {
        double width = Math.Max(260, canvas.Bounds.Width);
        bool isHigh = GetWaveformLevel(pointerPosition.X, width, probeIndex);
        LogicAnalyzerHoverTextBlock.Text = $"{signalName} logic level: {(isHigh ? "HIGH" : "LOW")} ({(isHigh ? "1" : "0")})";
    }

    private static bool GetWaveformLevel(double pointerX, double width, int probeIndex)
    {
        double step = width / 12;
        int segment = (int)Math.Floor(Math.Clamp(pointerX, 0, Math.Max(0, width - 1)) / step);
        bool highState = probeIndex % 2 == 0;

        for (int i = 0; i < segment; i++)
        {
            if (((i + probeIndex) % 3) != 1)
            {
                highState = !highState;
            }
        }

        return highState;
    }

    private static void DrawWaveform(Canvas canvas, IBrush foreground, int probeIndex)
    {
        canvas.Children.Clear();

        double width = Math.Max(260, canvas.Bounds.Width);
        double height = Math.Max(28, canvas.Bounds.Height);
        double low = height * 0.70;
        double high = height * 0.28;
        double step = width / 12;
        bool highState = probeIndex % 2 == 0;

        for (int i = 0; i <= 12; i++)
        {
            double x = i * step;
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(x, 0),
                EndPoint = new Point(x, height),
                Stroke = Brushes.DimGray,
                StrokeThickness = 0.5
            });
        }

        double xPosition = 0;
        double yPosition = highState ? high : low;
        for (int segment = 0; segment < 12; segment++)
        {
            double nextX = (segment + 1) * step;
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(xPosition, yPosition),
                EndPoint = new Point(nextX, yPosition),
                Stroke = foreground,
                StrokeThickness = 1.5
            });

            bool shouldToggle = ((segment + probeIndex) % 3) != 1;
            if (shouldToggle && segment < 11)
            {
                double nextY = yPosition == high ? low : high;
                canvas.Children.Add(new Line
                {
                    StartPoint = new Point(nextX, yPosition),
                    EndPoint = new Point(nextX, nextY),
                    Stroke = foreground,
                    StrokeThickness = 1.5
                });
                yPosition = nextY;
            }

            xPosition = nextX;
        }
    }

    public void RunStartupCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        _ = RunShellCommandAsync(new MacroDefinition
        {
            Name = "Startup",
            Command = command,
            Type = MacroTypes.Shell
        });
    }

    public void ShowSerialPlotter()
    {
        if (_serialPlotterWindow is { IsVisible: true })
        {
            _serialPlotterWindow.Activate();
            return;
        }

        _serialPlotterCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 220
        };
        _serialPlotterStatusTextBlock = new TextBlock
        {
            Text = "Waiting for numeric serial data.",
            Foreground = Brushes.Gray
        };
        Button clearButton = new()
        {
            Content = "Clear",
            MinWidth = 82
        };
        Button exportButton = new()
        {
            Content = "Export",
            MinWidth = 82
        };

        clearButton.Click += (_, _) =>
        {
            _serialPlotterSamples.Clear();
            RedrawSerialPlotter();
        };
        exportButton.Click += async (_, _) => await ExportSerialPlotterAsync();
        _serialPlotterCanvas.SizeChanged += (_, _) => RedrawSerialPlotter();

        Grid headerGrid = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8
        };
        headerGrid.Children.Add(new TextBlock
        {
            Text = "SERIAL PLOTTER --------------------------------",
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(exportButton, 1);
        Grid.SetColumn(clearButton, 2);
        headerGrid.Children.Add(exportButton);
        headerGrid.Children.Add(clearButton);

        Border plotBorder = new()
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12),
            Child = _serialPlotterCanvas
        };

        Grid content = new()
        {
            Margin = new Thickness(18),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(1, GridUnitType.Star)),
                new RowDefinition(GridLength.Auto)
            },
            RowSpacing = 10
        };
        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(plotBorder, 1);
        Grid.SetRow(_serialPlotterStatusTextBlock, 2);
        content.Children.Add(headerGrid);
        content.Children.Add(plotBorder);
        content.Children.Add(_serialPlotterStatusTextBlock);

        Window window = new()
        {
            Title = "Logicom Serial Plotter",
            Width = 680,
            Height = 420,
            MinWidth = 420,
            MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = content
        };

        _serialPlotterWindow = window;
        UtilityWindowCreated?.Invoke(window);
        window.Closed += (_, _) =>
        {
            if (_serialPlotterWindow == window)
            {
                _serialPlotterWindow = null;
                _serialPlotterCanvas = null;
                _serialPlotterStatusTextBlock = null;
                SerialPlotterToggled?.Invoke(false);
            }
        };

        window.Show();
        window.Activate();

        SerialPlotterToggled?.Invoke(true);
        RedrawSerialPlotter();
    }

    private async Task ExportSerialPlotterAsync()
    {
        if (_serialPlotterSamples.Count == 0)
        {
            return;
        }

        TopLevel? topLevel = _serialPlotterWindow ?? TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        FilePickerSaveOptions saveOptions = new()
        {
            Title = "Export Serial Plotter",
            SuggestedFileName = $"serial-plotter-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV files")
                {
                    Patterns = ["*.csv"]
                }
            ]
        };

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(saveOptions);
        if (file == null)
        {
            return;
        }

        await using Stream stream = await file.OpenWriteAsync();
        using StreamWriter writer = new(stream);
        await writer.WriteLineAsync("index,value");
        for (int i = 0; i < _serialPlotterSamples.Count; i++)
        {
            await writer.WriteLineAsync(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1}",
                i,
                _serialPlotterSamples[i]));
        }
    }

    private void ProcessSerialPlotterData(string data)
    {
        if (!TryParseSerialPlotterSample(data, out double sample))
        {
            return;
        }

        _serialPlotterSamples.Add(sample);
        TrimSerialPlotterSamples();

        RedrawSerialPlotter();
    }

    private void TrimSerialPlotterSamples()
    {
        int maxSamples = Math.Max(10, _settings.SerialPlotter.MaxSamples);
        if (_serialPlotterSamples.Count > maxSamples)
        {
            _serialPlotterSamples.RemoveRange(0, _serialPlotterSamples.Count - maxSamples);
        }
    }

    private static bool TryParseSerialPlotterSample(string data, out double sample)
    {
        char[] separators = [' ', '\t', ',', ';', ':'];
        foreach (string rawToken in data.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = rawToken;
            int equalsIndex = token.IndexOf('=');
            if (equalsIndex >= 0 && equalsIndex < token.Length - 1)
            {
                token = token[(equalsIndex + 1)..];
            }

            if (double.TryParse(
                token,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out sample))
            {
                return true;
            }
        }

        sample = 0;
        return false;
    }

    private void RedrawSerialPlotter()
    {
        Canvas? canvas = _serialPlotterCanvas;
        TextBlock? statusTextBlock = _serialPlotterStatusTextBlock;
        if (canvas == null || statusTextBlock == null)
        {
            return;
        }

        canvas.Children.Clear();

        double width = Math.Max(240, canvas.Bounds.Width);
        double height = Math.Max(180, canvas.Bounds.Height);
        DrawPlotterGrid(canvas, width, height);

        if (_serialPlotterSamples.Count < 2)
        {
            statusTextBlock.Text = _serialPlotterSamples.Count == 0
                ? "Waiting for numeric serial data."
                : $"Last: {_serialPlotterSamples[^1]:0.###}";
            return;
        }

        _settings.SerialPlotter.Normalize();
        int visibleCount = Math.Min(_serialPlotterSamples.Count, _settings.SerialPlotter.VisibleSamples);
        List<double> visibleSamples = _serialPlotterSamples
            .Skip(_serialPlotterSamples.Count - visibleCount)
            .ToList();
        double min = _settings.SerialPlotter.AutoScale
            ? visibleSamples.Min()
            : _settings.SerialPlotter.MinimumValue;
        double max = _settings.SerialPlotter.AutoScale
            ? visibleSamples.Max()
            : _settings.SerialPlotter.MaximumValue;
        if (Math.Abs(max - min) < 0.000001)
        {
            min -= 1;
            max += 1;
        }

        double xStep = width / Math.Max(1, visibleSamples.Count - 1);
        Point previous = ToPlotterPoint(visibleSamples[0], 0, xStep, min, max, height);
        for (int i = 1; i < visibleSamples.Count; i++)
        {
            Point next = ToPlotterPoint(visibleSamples[i], i, xStep, min, max, height);
            canvas.Children.Add(new Line
            {
                StartPoint = previous,
                EndPoint = next,
                Stroke = CreateProbeBrush(_settings.SerialPlotter.LineColor, 0),
                StrokeThickness = 1.5
            });
            previous = next;
        }

        double last = visibleSamples[^1];
        statusTextBlock.Text = $"Last: {last:0.###}   Min: {min:0.###}   Max: {max:0.###}   Samples: {_serialPlotterSamples.Count}";
    }

    private static void DrawPlotterGrid(Canvas canvas, double width, double height)
    {
        for (int i = 0; i <= 8; i++)
        {
            double x = width * i / 8;
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(x, 0),
                EndPoint = new Point(x, height),
                Stroke = Brushes.DimGray,
                StrokeThickness = 0.5
            });
        }

        for (int i = 0; i <= 4; i++)
        {
            double y = height * i / 4;
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(0, y),
                EndPoint = new Point(width, y),
                Stroke = Brushes.DimGray,
                StrokeThickness = 0.5
            });
        }
    }

    private static Point ToPlotterPoint(
        double value,
        int index,
        double xStep,
        double min,
        double max,
        double height)
    {
        double normalized = (value - min) / (max - min);
        return new Point(index * xStep, height - (normalized * height));
    }

    private static IBrush CreateProbeBrush(string color, int index)
    {
        if (TryParseHexColor(color, out Color parsedColor))
        {
            return new SolidColorBrush(parsedColor);
        }

        if (TryParseHexColor(LocalSettings.GetDefaultProbeColor(index), out Color defaultColor))
        {
            return new SolidColorBrush(defaultColor);
        }

        return Brushes.Cyan;
    }

    private static bool TryParseHexColor(string? colorText, out Color color)
    {
        color = default;
        string value = (colorText ?? "").Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
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

    private void UpdateStatusPanel()
    {
        bool isConnected = _monitor?.IsOpen == true;
        StatusConnectionTextBlock.Text = isConnected ? "Connected" : "Disconnected";
        StatusPortTextBlock.Text = isConnected
            ? _portName ?? "-"
            : PortComboBox.SelectedItem as string ?? "-";
        StatusBaudTextBlock.Text = isConnected
            ? _baudRate?.ToString() ?? "-"
            : BaudComboBox.SelectedItem?.ToString() ?? "-";
        StatusLineEndingTextBlock.Text = LineEndingComboBox.SelectedItem?.ToString() ?? "-";
        StatusMcuResetTextBlock.Text = _lastMcuResetAt.HasValue
            ? $"Done ({_lastMcuResetAt.Value:HH:mm:ss})"
            : "-";
        StatusUptimeTextBlock.Text = _connectedAt.HasValue
            ? FormatUptime(DateTime.Now - _connectedAt.Value)
            : "-";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        return uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}"
            : $"{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private static string AddTimestamps(string text, DateTime timestamp)
    {
        StringBuilder builder = new();

        string normalized = text.Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool isLastEmptyLine = i == lines.Length - 1 && line.Length == 0;
            if (isLastEmptyLine) break;
            if (line.Length > 0)
                builder.Append($"[{timestamp:HH:mm:ss}] {line}");
            if (i < lines.Length - 1) builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string FormatEntry(LogEntry entry, bool showTimestamp)
    {
        string text = GetEntryText(entry);

        if (!showTimestamp || !entry.AllowTimestamp)
        {
            return text;
        }

        return AddTimestamps(text, entry.Timestamp);
    }

    private static string GetEntryText(LogEntry entry)
    {
        StringBuilder builder = new();

        foreach (LogSegment segment in entry.Segments)
        {
            builder.Append(segment.Text);
        }

        return builder.ToString();
    }

    private string BuildLogText(bool showTimestamps)
    {
        StringBuilder builder = new();

        foreach (LogEntry entry in _logEntries)
        {
            builder.Append(FormatEntry(entry, showTimestamps));
        }

        return builder.ToString();
    }

    private void RenderOutput(bool scrollToActiveMatch = false)
    {
        InlineCollection outputInlines = GetOutputInlines();
        outputInlines.Clear();
        _renderedMatchIndex = 0;

        foreach (LogEntry entry in _logEntries)
        {
            AddEntryRuns(entry);
        }

        if (scrollToActiveMatch && TryGetActiveMatchLineIndex(out int lineIndex))
        {
            double lineHeight = OutputTextBlock.FontSize * 1.35;
            double centeredOffset = (lineIndex * lineHeight) - (OutputScrollViewer.Viewport.Height / 2) + (lineHeight / 2);
            OutputScrollViewer.Offset = OutputScrollViewer.Offset.WithY(Math.Max(0, centeredOffset));
            return;
        }

        OutputScrollViewer.ScrollToEnd();
    }

    private bool TryGetActiveMatchLineIndex(out int lineIndex)
    {
        lineIndex = 0;

        if (string.IsNullOrEmpty(_searchText) || _activeMatchIndex < 0)
        {
            return false;
        }

        string outputText = BuildLogText(_timestampsEnabled);
        int matchIndex = GetMatchIndex(outputText, _activeMatchIndex);
        if (matchIndex < 0)
        {
            return false;
        }

        for (int i = 0; i < matchIndex; i++)
        {
            if (outputText[i] == '\n')
            {
                lineIndex++;
            }
        }

        return true;
    }

    private int CountMatches()
    {
        if (string.IsNullOrEmpty(_searchText))
        {
            return 0;
        }

        return CountMatches(BuildLogText(_timestampsEnabled), _searchText);
    }

    private static int CountMatches(string text, string searchText)
    {
        int count = 0;
        int startIndex = 0;

        while (startIndex < text.Length)
        {
            int matchIndex = text.IndexOf(searchText, startIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return count;
            }

            count++;
            startIndex = matchIndex + searchText.Length;
        }

        return count;
    }

    private int GetMatchIndex(string text, int targetMatchIndex)
    {
        int matchIndex = -1;
        int startIndex = 0;

        for (int i = 0; i <= targetMatchIndex; i++)
        {
            matchIndex = text.IndexOf(_searchText, startIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return -1;
            }

            startIndex = matchIndex + _searchText.Length;
        }

        return matchIndex;
    }

    private void AppendOutput(
        string text,
        IBrush? foreground = null,
        bool bold = false,
        bool allowTimestamp = true,
        DateTime? timestamp = null)
    {
        LogEntry entry = new(
            [new LogSegment(text, foreground, bold)],
            timestamp ?? DateTime.Now,
            allowTimestamp);

        _logEntries.Add(entry);
        AddEntryRuns(entry);
        OutputScrollViewer.ScrollToEnd();
    }

    private void AppendErrorOutput(string label, string message, DateTime? timestamp = null)
    {
        AppendOutputSegments(
            [
                new LogSegment($"{label}: ", Brushes.Red, true),
                new LogSegment(message + "\n", null, false)
            ],
            timestamp: timestamp);
    }

    private void AppendStatusOutput(string label, string message, IBrush foreground)
    {
        string separator = string.IsNullOrWhiteSpace(message) ? "" : " ";
        AppendOutputSegments(
            [
                new LogSegment(label, foreground, true),
                new LogSegment(separator + message + "\n", null, false)
            ]);
    }

    private void AppendOutputSegments(
        IReadOnlyList<LogSegment> segments,
        bool allowTimestamp = true,
        DateTime? timestamp = null)
    {
        LogEntry entry = new(
            segments,
            timestamp ?? DateTime.Now,
            allowTimestamp);

        _logEntries.Add(entry);
        AddEntryRuns(entry);
        OutputScrollViewer.ScrollToEnd();
    }

    private void AddEntryRuns(LogEntry entry)
    {
        string entryText = GetEntryText(entry);

        if (_timestampsEnabled && entry.AllowTimestamp && !string.IsNullOrEmpty(entryText))
        {
            AddHighlightedRuns($"[{entry.Timestamp:HH:mm:ss}] ", OutputTextBlock.Foreground, bold: false);

            int consumed = 0;
            foreach (LogSegment segment in entry.Segments)
            {
                AddSegmentRunsWithTimestampPadding(
                    segment,
                    entryText.Length,
                    ref consumed);
            }

            return;
        }

        foreach (LogSegment segment in entry.Segments)
        {
            AddHighlightedRuns(
                segment.Text,
                segment.Foreground ?? OutputTextBlock.Foreground,
                segment.Bold);
        }
    }

    private void AddSegmentRunsWithTimestampPadding(
        LogSegment segment,
        int entryTextLength,
        ref int consumed)
    {
        IBrush? foreground = segment.Foreground ?? OutputTextBlock.Foreground;
        int startIndex = 0;

        while (startIndex < segment.Text.Length)
        {
            int newlineIndex = segment.Text.IndexOf('\n', startIndex);
            if (newlineIndex < 0)
            {
                string remainingText = segment.Text[startIndex..];
                AddHighlightedRuns(remainingText, foreground, segment.Bold);
                consumed += remainingText.Length;
                return;
            }

            string lineText = segment.Text[startIndex..newlineIndex];
            AddHighlightedRuns(lineText, foreground, segment.Bold);
            AddHighlightedRuns("\n", foreground, segment.Bold);

            consumed += lineText.Length + 1;
            if (consumed < entryTextLength)
            {
                AddHighlightedRuns(TimestampColumnPadding, OutputTextBlock.Foreground, bold: false);
            }

            startIndex = newlineIndex + 1;
        }
    }

    private void AddHighlightedRuns(string text, IBrush? foreground, bool bold)
    {
        if (string.IsNullOrEmpty(_searchText))
        {
            AddTextRun(text, foreground, bold);
            return;
        }

        int startIndex = 0;

        while (startIndex < text.Length)
        {
            int matchIndex = text.IndexOf(_searchText, startIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                AddTextRun(text[startIndex..], foreground, bold);
                return;
            }

            if (matchIndex > startIndex)
            {
                AddTextRun(text[startIndex..matchIndex], foreground, bold);
            }

            bool isActiveMatch = _renderedMatchIndex == _activeMatchIndex;
            AddTextRun(
                text.Substring(matchIndex, _searchText.Length),
                Brushes.Black,
                bold: true,
                isActiveMatch ? Brushes.Orange : Brushes.Yellow);

            _renderedMatchIndex++;

            startIndex = matchIndex + _searchText.Length;
        }
    }

    private void AddTextRun(
        string text,
        IBrush? foreground,
        bool bold,
        IBrush? background = null)
    {
        if (text.Length == 0)
        {
            return;
        }

        Run run = new()
        {
            Text = text,
            Foreground = foreground,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            Background = background
        };

        GetOutputInlines().Add(run);
    }

    private void ClearOutput()
    {
        _logEntries.Clear();
        GetOutputInlines().Clear();
    }

    private async Task ConfirmAndClearOutput()
    {
        Window? window = Window.GetTopLevel(this) as Window;
        if (window == null) return;

        bool confirm = false;

        Button yesButton = new() { Content = "Yes", Width = 80 };
        Button noButton = new() { Content = "No", Width = 80 };

        Window dialog = new()
        {
            Title = "Clear Output",
            Width = 300,
            Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Are you sure you want to clear the output log?",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yesButton, noButton }
                    }
                }
            }
        };

        yesButton.Click += (_, _) =>
        {
            confirm = true;
            dialog.Close();
        };

        noButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(window);

        if (confirm)
        {
            ClearOutput();
        }
    }

    private InlineCollection GetOutputInlines()
    {
        return OutputTextBlock.Inlines ??= new InlineCollection();
    }

    public async Task ShowFindWindowAsync()
    {
        Window? window = Window.GetTopLevel(this) as Window;
        if (window == null) return;

        if (_findWindow is { IsVisible: true })
        {
            _findWindow.Activate();
            _findTextBox?.Focus();
            _findTextBox?.SelectAll();
            return;
        }

        TextBox findTextBox = new()
        {
            Width = 280,
            Text = _searchText,
            PlaceholderText = "Find"
        };
        _findTextBox = findTextBox;

        TextBlock statusTextBlock = new()
        {
            Text = GetFindStatusText()
        };

        Button clearButton = new()
        {
            Content = "Clear",
            Width = 80
        };

        Button findButton = new()
        {
            Content = "Find",
            Width = 80
        };

        Button closeButton = new()
        {
            Content = "Close",
            Width = 80
        };

        Window findWindow = new()
        {
            Title = "Find",
            Width = 440,
            Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    findTextBox,
                    statusTextBlock,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            findButton,
                            clearButton,
                            closeButton
                        }
                    }
                }
            }
        };

        _findWindow = findWindow;
        UtilityWindowCreated?.Invoke(findWindow);

        void UpdateStatus()
        {
            statusTextBlock.Text = GetFindStatusText();
        }

        void ApplyFind(bool advance)
        {
            string requestedSearchText = findTextBox.Text ?? "";
            bool searchChanged = !string.Equals(_searchText, requestedSearchText, StringComparison.Ordinal);

            _searchText = requestedSearchText;
            _matchCount = CountMatches();

            if (_matchCount == 0)
            {
                _activeMatchIndex = -1;
            }
            else if (searchChanged || _activeMatchIndex < 0)
            {
                _activeMatchIndex = 0;
            }
            else if (advance)
            {
                _activeMatchIndex = (_activeMatchIndex + 1) % _matchCount;
            }

            UpdateStatus();
            RenderOutput(scrollToActiveMatch: _matchCount > 0);
            findTextBox.Focus();
        }

        findTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyFind(advance: true);
            }
            else if (e.Key == Key.Escape)
            {
                findWindow.Close();
            }
        };

        findButton.Click += (_, _) => ApplyFind(advance: true);

        clearButton.Click += (_, _) =>
        {
            findTextBox.Text = "";
            _searchText = "";
            _matchCount = 0;
            _activeMatchIndex = -1;
            UpdateStatus();
            RenderOutput();
            findTextBox.Focus();
        };

        closeButton.Click += (_, _) => findWindow.Close();

        findWindow.Closed += (_, _) =>
        {
            _findWindow = null;
            _findTextBox = null;
        };

        findWindow.Show(window);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            findTextBox.Focus();
            findTextBox.SelectAll();
        });
    }

    private string GetFindStatusText()
    {
        if (string.IsNullOrEmpty(_searchText))
        {
            return "No search active.";
        }

        if (_matchCount == 0)
        {
            return "No matches.";
        }

        return $"{_activeMatchIndex + 1} of {_matchCount} matches";
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        Window? window = Window.GetTopLevel(this) as Window;
        if (window == null) return;

        Button okButton = new()
        {
            Content = "OK",
            Width = 90,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        Window dialog = new()
        {
            Title = title,
            Width = 360,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    okButton
                }
            }
        };

        okButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(window);
    }

    public async Task SaveLogAsync()
    {
        string logText = BuildLogText(showTimestamps: true);

        if (string.IsNullOrWhiteSpace(logText))
        {
            await ShowMessageAsync(
                "Nothing to Save",
                "There is no serial output to save yet."
            );

            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);

        if (topLevel == null)
        {
            AppendOutput("Could not open save dialog.\n");
            return;
        }

        IStorageFolder? suggestedStartLocation = null;

        if (!string.IsNullOrWhiteSpace(_settings.DefaultLogSaveLocation)
            && Directory.Exists(_settings.DefaultLogSaveLocation))
        {
            suggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(
                _settings.DefaultLogSaveLocation);
        }

        FilePickerSaveOptions saveOptions = new()
        {
            Title = "Save Serial Log",
            SuggestedFileName = $"serial-log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log",
            SuggestedStartLocation = suggestedStartLocation,
            DefaultExtension = "log",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Log files")
                {
                    Patterns = new[] { "*.log" }
                },
                new FilePickerFileType("Text files")
                {
                    Patterns = new[] { "*.txt" }
                }
            }
        };

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(saveOptions);

        if (file == null)
        {
            return;
        }

        try
        {
            await using Stream stream = await file.OpenWriteAsync();
            using StreamWriter writer = new(stream);

            await writer.WriteAsync(logText);

            AppendOutput($"Saved log to {file.Name}\n");
        }
        catch (Exception ex)
        {
            AppendErrorOutput("Save failed", ex.Message);
        }
    }

    public async Task CopySerialMonitorAsync()
    {
        string logText = BuildLogText(showTimestamps: true);

        if (string.IsNullOrEmpty(logText))
        {
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null)
        {
            AppendOutput("Could not access clipboard.\n");
            return;
        }

        await topLevel.Clipboard.SetTextAsync(logText);
    }

    public bool ToggleTimestamps()
    {
        _timestampsEnabled = !_timestampsEnabled;
        RenderOutput();

        string state = _timestampsEnabled ? "enabled" : "disabled";
        TimestampsToggled?.Invoke(_timestampsEnabled);

        return _timestampsEnabled;
    }

    public void Dispose()
    {
        _isDisposing = true;
        _uptimeTimer.Stop();
        _findWindow?.Close();
        _signalViewerWindow?.Close();
        _statusPanelWindow?.Close();
        _serialPlotterWindow?.Close();
        _monitor?.Dispose();
    }

    private sealed record LogEntry(
        IReadOnlyList<LogSegment> Segments,
        DateTime Timestamp,
        bool AllowTimestamp);

    private sealed record LogSegment(
        string Text,
        IBrush? Foreground,
        bool Bold);
}
