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
    private TextBox? _findTextBox;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly List<LogEntry> _logEntries = [];
    private readonly List<MacroDefinition> _macroAutocompleteMatches = [];
    private bool _timestampsEnabled;
    private bool _suppressMacroAutocomplete;
    private string _searchText = "";
    private int _matchCount;
    private int _activeMatchIndex = -1;
    private int _renderedMatchIndex;

    private string? _portName;
    private int? _baudRate;
    private DateTime? _connectedAt;
    private DateTime? _mcuResetRequestedAt;
    private DateTime? _lastMcuResetAt;
    private DateTime? _lastRxAt;
    private DateTime? _lastTxAt;
    private bool _isResettingConnection;
    private bool _reconnectInProgress;
    private string? _resetPortName;
    private int? _resetBaudRate;
    private long _rxBytes;
    private long _txBytes;
    private int _rxLines;
    private int _txCommands;
    private int _serialErrors;
    private string _lastError = "None";
    private string _boardName = "-";
    private string _mcuName = "-";
    private string _targetVoltage = "-";
    private string _deviceId = "-";
    private string _deviceCpu = "-";
    private McuResetState _mcuResetState = McuResetState.None;

    private LocalSettings _settings = new();

    public event Action<bool>? TimestampsToggled;
    public event Action<bool>? SignalViewerDetachedChanged;
    public event Action<bool>? StatusPanelDetachedChanged;
    public event Action<bool>? SerialPlotterToggled;
    public event Action<Window>? UtilityWindowCreated;
    public event Action<string?, int?>? ConnectionChanged;
    public event Action? StatusSettingsRequested;
    public event Action<string>? SerialDataReceived;

    public bool TimestampsEnabled => _timestampsEnabled;
    public bool IsSignalViewerVisible => false;
    public bool IsSignalViewerDetached => false;
    public bool IsStatusPanelDetached => false;
    public bool IsSerialPlotterVisible => false;

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
        ConfigureStatusPanelContextMenu();
        ConfigureSerialMonitorContextMenu();
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
        string? selectedPort = PortComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selectedPort))
        {
            AppendOutput("No port selected.\n");
            return;
        }

        if (BaudComboBox.SelectedItem is not int baudRate)
        {
            AppendOutput("No baud rate selected.\n");
            return;
        }

        if (TryOpenSerialMonitor(selectedPort, baudRate, out string? errorMessage))
        {
            AppendStatusOutput("Connected", $"to {selectedPort} at {baudRate} baud.", Brushes.LimeGreen);
            return;
        }

        RecordSerialError(errorMessage ?? "Unknown connection error.");
        AppendErrorOutput("Connect failed", errorMessage ?? "Unknown connection error.");
    }

    private bool TryOpenSerialMonitor(string portName, int baudRate, out string? errorMessage)
    {
        errorMessage = null;

        SerialMonitor? monitor = null;

        try
        {
            monitor = new SerialMonitor(portName, baudRate, CreateSerialPortSettings());
            monitor.RawDataReceived += data =>
            {
                DateTime receivedAt = DateTime.Now;
                Dispatcher.UIThread.Post(() =>
                {
                    RecordReceivedBytes(data, receivedAt);
                    AppendOutput(data, timestamp: receivedAt);
                });
            };
            monitor.DataReceived += data =>
            {
                DateTime receivedAt = DateTime.Now;
                Dispatcher.UIThread.Post(() =>
                {
                    RecordReceivedLine(data, receivedAt);
                    SerialDataReceived?.Invoke(data);
                });
            };
            monitor.ErrorReceived += ex =>
            {
                DateTime receivedAt = DateTime.Now;
                Dispatcher.UIThread.Post(() =>
                {
                    bool shouldReconnect = RecordSerialError(ex.Message);
                    AppendErrorOutput("Serial error", ex.Message, receivedAt);

                    if (shouldReconnect)
                    {
                        CloseMonitorForReset();
                        _ = TryReconnectAfterResetAsync();
                    }
                });
            };
            monitor.Open();

            _monitor = monitor;
            _portName = portName;
            _baudRate = baudRate;
            _connectedAt = DateTime.Now;
            _isResettingConnection = false;
            _uptimeTimer.Start();

            SelectConnectionValues(portName, baudRate);
            SetConnectionUiState(isConnected: true);
            ConnectionChanged?.Invoke(_portName, baudRate);
            return true;
        }
        catch (Exception ex)
        {
            monitor?.Dispose();
            errorMessage = ex.Message;
            return false;
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
        bool isResetCommand = IsMcuResetProcessCommand(label, macro.Command);
        AppendStatusOutput("$ ", $"{label}: {macro.Command}", Brushes.DeepSkyBlue);

        try
        {
            if (isResetCommand)
            {
                BeginMcuResetOperation(closeCurrentConnection: true);
            }

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
                if (isResetCommand)
                {
                    MarkMcuResetFailed("Process did not start.");
                    await TryReconnectAfterResetAsync();
                }

                AppendErrorOutput("Command failed", "Process did not start.");
                return;
            }

            Task outputTask = ReadProcessOutputAsync(process.StandardOutput);
            Task errorTask = ReadProcessOutputAsync(process.StandardError, Brushes.Red);

            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);

            if (process.ExitCode == 0)
            {
                AppendStatusOutput("Finished", $"{label} exited with code 0.", Brushes.LimeGreen);
                if (isResetCommand)
                {
                    MarkMcuResetSuccess(DateTime.Now);
                    await TryReconnectAfterResetAsync();
                }
            }
            else
            {
                if (isResetCommand)
                {
                    MarkMcuResetFailed($"{label} exited with code {process.ExitCode}.");
                    await TryReconnectAfterResetAsync();
                }

                AppendErrorOutput("Command failed", $"{label} exited with code {process.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            if (isResetCommand)
            {
                MarkMcuResetFailed(ex.Message);
                await TryReconnectAfterResetAsync();
            }

            AppendErrorOutput("Command failed", ex.Message);
        }
    }

    private async Task ReadProcessOutputAsync(StreamReader reader, IBrush? defaultForeground = null)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            string output = line + "\n";
            if (Dispatcher.UIThread.CheckAccess())
            {
                AppendProcessOutput(output, defaultForeground);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() => AppendProcessOutput(output, defaultForeground));
            }
        }
    }

    private void AppendProcessOutput(string output, IBrush? defaultForeground = null)
    {
        ProcessStatusText(output, DateTime.Now);
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

    [GeneratedRegex(@"(^|[\s_/\-])(mcu\s*)?reset($|[\s_/\-])", RegexOptions.IgnoreCase)]
    private static partial Regex McuResetCommandRegex();

    [GeneratedRegex(@"\b(fail(ed)?|error|timeout|denied|unable)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResetFailureRegex();

    [GeneratedRegex(@"^\s*Software\s+reset\s+is\s+performed\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SoftwareResetPerformedRegex();

    [GeneratedRegex(@"^\s*Finished\s+.*\bRESET\b.*\s+exited\s+with\s+code\s+0\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResetExitSuccessRegex();

    [GeneratedRegex(@"^\s*Board\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BoardLineRegex();

    [GeneratedRegex(@"^\s*Voltage\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VoltageLineRegex();

    [GeneratedRegex(@"^\s*Device\s+ID\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdLineRegex();

    [GeneratedRegex(@"^\s*Device\s+name\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceNameLineRegex();

    [GeneratedRegex(@"^\s*Device\s+CPU\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceCpuLineRegex();

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
            RecordSentCommand(command, lineEnding);
        }
        catch (Exception ex)
        {
            RecordSerialError(ex.Message);
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

    private void RecordSentCommand(string command, SerialLineEnding lineEnding)
    {
        _txCommands++;
        _lastTxAt = DateTime.Now;
        _txBytes += Encoding.UTF8.GetByteCount(command + GetLineEndingText(lineEnding));

        if (IsMcuResetCommand(command))
        {
            BeginMcuResetOperation(closeCurrentConnection: false, requestedAt: _lastTxAt);
        }

        UpdateStatusPanel();
    }

    private void RecordReceivedBytes(string data, DateTime receivedAt)
    {
        _lastRxAt = receivedAt;
        _rxBytes += Encoding.UTF8.GetByteCount(data);
        ProcessStatusText(data, receivedAt);
        UpdateStatusPanel();
    }

    private void RecordReceivedLine(string data, DateTime receivedAt)
    {
        _lastRxAt = receivedAt;
        _rxLines++;
        ProcessStatusText(data, receivedAt);
        UpdateStatusPanel();
    }

    private bool RecordSerialError(string message)
    {
        _serialErrors++;
        _lastError = string.IsNullOrWhiteSpace(message) ? "Unknown" : message.Trim();

        bool resetRelated = _isResettingConnection || _mcuResetState == McuResetState.Pending;
        if (resetRelated)
        {
            EnsureResetReconnectTarget();
            _isResettingConnection = true;
        }

        UpdateStatusPanel();
        return resetRelated;
    }

    private static string GetLineEndingText(SerialLineEnding lineEnding)
    {
        return lineEnding switch
        {
            SerialLineEnding.LF => "\n",
            SerialLineEnding.CR => "\r",
            SerialLineEnding.CRLF => "\r\n",
            _ => ""
        };
    }

    private static int CountReceivedLines(string data)
    {
        string normalized = data.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static bool IsMcuResetCommand(string command)
    {
        string normalized = command.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        return McuResetCommandRegex().IsMatch(normalized)
            || normalized.Contains("sysreset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "nrst", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMcuResetProcessCommand(string label, string command)
    {
        return IsMcuResetCommand(label)
            || IsMcuResetCommand(command)
            || (command.Contains("STM32_Programmer_CLI", StringComparison.OrdinalIgnoreCase)
                && (command.Contains("rst", StringComparison.OrdinalIgnoreCase)
                    || command.Contains("reset", StringComparison.OrdinalIgnoreCase)));
    }

    private void ProcessStatusText(string data, DateTime timestamp)
    {
        string cleanText = AnsiEscapeRegex().Replace(data, "");
        string normalized = cleanText.Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (string line in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ProcessStatusLine(line, timestamp);
        }
    }

    private void ProcessStatusLine(string line, DateTime timestamp)
    {
        string text = line.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (TryApplyDeviceLine(BoardLineRegex().Match(text), value => _boardName = value)
            || TryApplyDeviceLine(VoltageLineRegex().Match(text), value => _targetVoltage = value)
            || TryApplyDeviceLine(DeviceIdLineRegex().Match(text), value => _deviceId = value)
            || TryApplyDeviceLine(DeviceNameLineRegex().Match(text), value => _mcuName = value)
            || TryApplyDeviceLine(DeviceCpuLineRegex().Match(text), value => _deviceCpu = value))
        {
            return;
        }

        if (SoftwareResetPerformedRegex().IsMatch(text) || ResetExitSuccessRegex().IsMatch(text))
        {
            MarkMcuResetSuccess(timestamp);
            return;
        }

        if ((_mcuResetState == McuResetState.Pending || _isResettingConnection)
            && !IsLikelyResetEcho(text)
            && ResetFailureRegex().IsMatch(text))
        {
            MarkMcuResetFailed(text);
        }
    }

    private static bool TryApplyDeviceLine(Match match, Action<string> apply)
    {
        if (!match.Success)
        {
            return false;
        }

        string value = match.Groups[1].Value.Trim();
        if (value.Length > 0)
        {
            apply(value);
        }

        return true;
    }

    private static bool IsLikelyResetEcho(string text)
    {
        string normalized = text.Trim().Trim('>', ' ');
        return string.Equals(normalized, "reset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "stm32 reset", StringComparison.OrdinalIgnoreCase);
    }

    private void BeginMcuResetOperation(bool closeCurrentConnection, DateTime? requestedAt = null)
    {
        DateTime timestamp = requestedAt ?? DateTime.Now;
        _mcuResetRequestedAt = timestamp;
        _lastMcuResetAt = null;
        _mcuResetState = McuResetState.Pending;
        _isResettingConnection = true;
        EnsureResetReconnectTarget();

        if (closeCurrentConnection)
        {
            CloseMonitorForReset();
        }

        UpdateStatusPanel();
    }

    private void EnsureResetReconnectTarget()
    {
        _resetPortName ??= _portName ?? (PortComboBox.SelectedItem as string);
        if (!_resetBaudRate.HasValue)
        {
            _resetBaudRate = _baudRate ?? (BaudComboBox.SelectedItem is int selectedBaud ? selectedBaud : null);
        }
    }

    private void MarkMcuResetSuccess(DateTime timestamp)
    {
        _lastMcuResetAt = timestamp;
        _mcuResetState = McuResetState.Success;
        _isResettingConnection = _monitor?.IsOpen != true && !string.IsNullOrWhiteSpace(_resetPortName);
        UpdateStatusPanel();
    }

    private void MarkMcuResetFailed(string message)
    {
        _mcuResetState = McuResetState.Failed;
        _isResettingConnection = false;
        _lastError = string.IsNullOrWhiteSpace(message) ? "Reset failed." : message.Trim();
        UpdateStatusPanel();
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

    private void SelectConnectionValues(string portName, int baudRate)
    {
        if (!Equals(PortComboBox.SelectedItem, portName))
        {
            PortComboBox.SelectedItem = portName;
        }

        if (!Equals(BaudComboBox.SelectedItem, baudRate))
        {
            BaudComboBox.SelectedItem = baudRate;
        }
    }

    private void CloseMonitorForReset()
    {
        _monitor?.Dispose();
        _monitor = null;
        _connectedAt = null;
        _uptimeTimer.Stop();
        SetConnectionUiState(isConnected: false);
    }

    private async Task TryReconnectAfterResetAsync()
    {
        if (_reconnectInProgress)
        {
            return;
        }

        EnsureResetReconnectTarget();
        if (string.IsNullOrWhiteSpace(_resetPortName) || !_resetBaudRate.HasValue)
        {
            _isResettingConnection = false;
            UpdateStatusPanel();
            return;
        }

        string portName = _resetPortName;
        int baudRate = _resetBaudRate.Value;
        _reconnectInProgress = true;

        try
        {
            string lastReconnectError = $"Port {portName} was not found after reset.";

            for (int attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(attempt == 0 ? 400 : 650);

                string[] ports = SerialMonitor.GetAvailablePorts();
                if (!ports.Contains(portName, StringComparer.Ordinal))
                {
                    lastReconnectError = $"Port {portName} is not available.";
                    continue;
                }

                bool connected = false;
                string? connectError = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    connected = TryOpenSerialMonitor(portName, baudRate, out connectError);
                });

                if (connected)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _resetPortName = null;
                        _resetBaudRate = null;
                        _isResettingConnection = false;
                        AppendStatusOutput("Reconnected", $"to {portName} at {baudRate} baud.", Brushes.LimeGreen);
                        UpdateStatusPanel();
                    });
                    return;
                }

                lastReconnectError = connectError ?? "Reconnect failed.";
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _isResettingConnection = false;
                _lastError = lastReconnectError;
                if (_mcuResetState == McuResetState.Pending)
                {
                    _mcuResetState = McuResetState.Failed;
                }

                SetConnectionUiState(isConnected: false);
                ConnectionChanged?.Invoke(null, null);
                AppendErrorOutput("Reconnect failed", lastReconnectError);
            });
        }
        finally
        {
            _reconnectInProgress = false;
        }
    }

    private void Disconnect()
    {
        _monitor?.Dispose();
        _monitor = null;
        _uptimeTimer.Stop();
        _connectedAt = null;
        _isResettingConnection = false;
        _resetPortName = null;
        _resetBaudRate = null;

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
        SignalViewerDetachedChanged?.Invoke(false);
        return false;
    }

    public bool ToggleSignalViewerDetached()
    {
        SignalViewerDetachedChanged?.Invoke(false);
        return false;
    }

    private void DetachSignalViewer()
    {
        SignalViewerDetachedChanged?.Invoke(false);
    }

    private void AttachSignalViewer()
    {
        SignalViewerDetachedChanged?.Invoke(false);
    }

    private void DetachStatusPanel()
    {
        SetStatusPanelVisible(true);
    }

    private void AttachStatusPanel(bool isVisible = true)
    {
        SetStatusPanelVisible(isVisible);
    }

    public bool ToggleStatusPanelDetached()
    {
        SetStatusPanelVisible(true);
        StatusPanelDetachedChanged?.Invoke(false);
        return false;
    }

    private void CloseStatusPanel()
    {
        SetStatusPanelVisible(false);
        StatusPanelDetachedChanged?.Invoke(false);
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
        SignalViewerSplitter.IsVisible = false;
    }

    private void ConfigureSignalViewerContextMenu()
    {
        SignalViewerPanel.ContextMenu = null;
    }

    public void ShowStatusPanel()
    {
        SetStatusPanelVisible(true);
        StatusPanelDetachedChanged?.Invoke(false);
    }

    private void SetStatusPanelVisible(bool isVisible)
    {
        StatusPanel.IsVisible = isVisible;

        if (MainContentGrid.ColumnDefinitions.Count > 1)
        {
            MainContentGrid.ColumnDefinitions[1].Width = isVisible
                ? new GridLength(340)
                : new GridLength(0);
        }

        MainContentGrid.ColumnSpacing = isVisible ? 12 : 0;
    }

    private void ConfigureStatusPanelContextMenu()
    {
        MenuItem closeItem = new()
        {
            Header = "Close Status Panel"
        };
        MenuItem customizeItem = new()
        {
            Header = "Customize Status Settings..."
        };

        closeItem.Click += (_, _) => Dispatcher.UIThread.Post(CloseStatusPanel);
        customizeItem.Click += (_, _) => StatusSettingsRequested?.Invoke();

        StatusPanel.ContextMenu = new ContextMenu
        {
            Items =
            {
                closeItem,
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
        UpdateStatusPanel();
    }

    public void UpdateSerialPlotterSettings(SerialPlotterSettings settings)
    {
        settings.Normalize();
        _settings.SerialPlotter = settings;
        LocalSettings.Save(_settings);
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
    }

    private void UpdateWaveformLayoutHeight()
    {
        WaveformRowsGrid.MinHeight = Math.Max(0, WaveformScrollViewer.Bounds.Height);
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
        SerialPlotterToggled?.Invoke(false);
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
        string connectionState = _isResettingConnection || _mcuResetState == McuResetState.Pending
            ? "Resetting..."
            : isConnected
                ? "Connected"
                : "Disconnected";
        string port = isConnected
            ? _portName ?? "-"
            : PortComboBox.SelectedItem as string ?? "-";
        string baudRate = isConnected
            ? _baudRate?.ToString(CultureInfo.InvariantCulture) ?? "-"
            : BaudComboBox.SelectedItem?.ToString() ?? "-";
        string lineEnding = LineEndingComboBox.SelectedItem?.ToString() ?? "-";
        string uptime = _connectedAt.HasValue
            ? FormatUptime(DateTime.Now - _connectedAt.Value)
            : "-";
        string logText = BuildLogText(showTimestamps: false);

        StringBuilder builder = new();
        AppendStatusSection(builder, "STATUS");
        AppendStatusRow(builder, "Connection", connectionState);
        AppendStatusRow(builder, "Uptime", uptime);
        builder.AppendLine();

        AppendStatusSection(builder, "SERIAL");
        AppendStatusRow(builder, "Port", port);
        AppendStatusRow(builder, "Baud Rate", baudRate);
        AppendStatusRow(builder, "Line Ending", lineEnding);
        AppendStatusRow(builder, "Flow Control", _settings.StatusPanel.FlowControl);
        builder.AppendLine();

        AppendStatusSection(builder, "TRAFFIC");
        AppendStatusRow(builder, "RX Bytes", _rxBytes.ToString("N0", CultureInfo.InvariantCulture));
        AppendStatusRow(builder, "TX Bytes", _txBytes.ToString("N0", CultureInfo.InvariantCulture));
        AppendStatusRow(builder, "RX Lines", _rxLines.ToString("N0", CultureInfo.InvariantCulture));
        AppendStatusRow(builder, "TX Commands", _txCommands.ToString("N0", CultureInfo.InvariantCulture));
        AppendStatusRow(builder, "Last RX", FormatRelativeTime(_lastRxAt));
        AppendStatusRow(builder, "Last TX", FormatRelativeTime(_lastTxAt));
        builder.AppendLine();

        AppendStatusSection(builder, "DEVICE");
        AppendStatusRow(builder, "Board", _boardName);
        AppendStatusRow(builder, "MCU", _mcuName);
        AppendStatusRow(builder, "Target Voltage", _targetVoltage);
        AppendStatusRow(builder, "MCU Reset", FormatMcuResetStatus());
        builder.AppendLine();

        AppendStatusSection(builder, "LOG");
        AppendStatusRow(builder, "Timestamps", _timestampsEnabled ? "On" : "Off");
        AppendStatusRow(builder, "Auto-scroll", "On");
        AppendStatusRow(builder, "Log Lines", CountReceivedLines(logText).ToString("N0", CultureInfo.InvariantCulture));
        AppendStatusRow(builder, "Log Size", FormatByteSize(Encoding.UTF8.GetByteCount(logText)));
        builder.AppendLine();

        AppendStatusSection(builder, "ERRORS");
        AppendStatusRow(builder, "Serial Errors", _serialErrors.ToString("N0", CultureInfo.InvariantCulture));
        AppendStatusRow(builder, "Last Error", _lastError);

        StatusPanelTextBlock.Text = builder.ToString();
    }

    private string FormatMcuResetStatus()
    {
        return _mcuResetState switch
        {
            McuResetState.Pending => _mcuResetRequestedAt.HasValue
                ? $"Pending ({FormatRelativeTime(_mcuResetRequestedAt)})"
                : "Pending",
            McuResetState.Success => _lastMcuResetAt.HasValue
                ? $"Success ({_lastMcuResetAt.Value:HH:mm:ss})"
                : "Success",
            McuResetState.Failed => "Failed",
            _ => "-"
        };
    }

    private static void AppendStatusSection(StringBuilder builder, string title)
    {
        builder.Append(title);
        builder.Append(' ');
        builder.AppendLine(new string('-', Math.Max(1, 32 - title.Length)));
    }

    private static void AppendStatusRow(StringBuilder builder, string label, string value)
    {
        builder.Append(label.PadRight(16));
        builder.Append(": ");
        builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static string FormatRelativeTime(DateTime? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return "-";
        }

        TimeSpan elapsed = DateTime.Now - timestamp.Value;
        if (elapsed.TotalSeconds < 2)
        {
            return "just now";
        }

        return FormatUptime(elapsed) + " ago";
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double kib = bytes / 1024.0;
        if (kib < 1024)
        {
            return kib.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return (kib / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
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
        UpdateStatusPanel();
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
        ProcessStatusText(label + separator + message + "\n", DateTime.Now);
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
        UpdateStatusPanel();
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
        UpdateStatusPanel();
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
        UpdateStatusPanel();

        string state = _timestampsEnabled ? "enabled" : "disabled";
        TimestampsToggled?.Invoke(_timestampsEnabled);

        return _timestampsEnabled;
    }

    public void Dispose()
    {
        _uptimeTimer.Stop();
        _findWindow?.Close();
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

    private enum McuResetState
    {
        None,
        Pending,
        Success,
        Failed
    }
}
