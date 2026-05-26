using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using serial.Core;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace serial.Desktop;

public partial class MainWindow : Window
{
    private const string TimestampColumnPadding = "           ";

    private SerialMonitor? _monitor;
    private Window? _findWindow;
    private TextBox? _findTextBox;
    private SettingsWindow? _settingsWindow;
    private LocalSettings _settings = new();
    private readonly List<LogEntry> _logEntries = [];
    private bool _timestampsEnabled;
    private string _searchText = "";
    private int _matchCount;
    private int _activeMatchIndex = -1;
    private int _renderedMatchIndex;

    private string? _portName;
    private int? _baudRate;

    public event Action? NewWindowRequested;

    public event Action<bool>? TimestampsToggled;

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
            _findWindow?.SetValue(FontFamilyProperty, FontFamily);
            _settingsWindow?.SetValue(FontFamilyProperty, FontFamily);
            WriteFontAvailability(fontFamily);
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
        _settings = LocalSettings.Load();
        AppFontFamily = _settings.FontFamily;

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

        RefreshButton.Click += (_, _) => RefreshPorts();
        ConnectButton.Click += (_, _) => ToggleConnection();
        SendButton.Click += (_, _) => SendCommand();
        ClearButton.Click += (_, _) => ClearOutput();

        CommandTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SendCommand();
            }
        };

        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                e.Handled = true;
                await ShowFindWindowAsync();
            }
            else if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
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

        SetConnectionUiState(isConnected: false);
        RenderMacroButtons();

        RefreshPorts();
    }

    private static void WriteFontAvailability(string fontFamily)
    {
        string requestedFamily = fontFamily
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";

        if (string.IsNullOrWhiteSpace(requestedFamily))
        {
            Console.WriteLine("Font family setting is empty; using default fallback.");
            return;
        }

        Typeface requestedTypeface = new(new FontFamily(fontFamily));
        if (!FontManager.Current.TryGetGlyphTypeface(requestedTypeface, out GlyphTypeface? glyphTypeface))
        {
            Console.WriteLine($"Font family not resolved: {requestedFamily}");
            return;
        }

        bool resolvedRequestedFamily =
            string.Equals(glyphTypeface.FamilyName, requestedFamily, StringComparison.OrdinalIgnoreCase)
            || string.Equals(glyphTypeface.TypographicFamilyName, requestedFamily, StringComparison.OrdinalIgnoreCase)
            || glyphTypeface.FamilyNames.Any(family =>
                string.Equals(family.Value, requestedFamily, StringComparison.OrdinalIgnoreCase));

        string status = resolvedRequestedFamily ? "found" : "using fallback";
        Console.WriteLine(
            $"Font family {status}: requested '{requestedFamily}', resolved '{glyphTypeface.FamilyName}'.");
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
            _monitor = new SerialMonitor(_portName, baudRate);
            _monitor.DataReceived += data =>
            {
                DateTime receivedAt = DateTime.Now;
                Dispatcher.UIThread.Post(() =>
                {
                    AppendOutput(data + "\n", timestamp: receivedAt);
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

            AppendStatusOutput("Connected", $"to {_portName} at {baudRate} baud.", Brushes.LimeGreen);
            SetConnectionUiState(isConnected: true);
        }
        catch (Exception ex)
        {
            AppendErrorOutput("Connect failed", ex.Message);
        }
    }

    private void SendCommand()
    {
        if (_monitor?.IsOpen != true)
        {
            AppendOutput("Not connected.\n");
            return;
        }

        string? command = CommandTextBox.Text;
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        CommandTextBox.Text = "";
        SendCommandText(command);
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

        SendCommandText(macro.Command);
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

    private void SendCommandText(string command)
    {
        if (_monitor?.IsOpen != true)
        {
            AppendOutput("Not connected.\n");
            return;
        }

        AppendOutput($"> {command}\n");

        try
        {
            if (LineEndingComboBox.SelectedItem is not SerialLineEnding lineEnding)
            {
                lineEnding = SerialLineEnding.None;
            }

            _monitor.Write(command, lineEnding);
        }
        catch (Exception ex)
        {
            AppendErrorOutput("Send failed", ex.Message);
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
        RenderMacroButtons();
    }

    private void RenderMacroButtons()
    {
        MacroButtonsPanel.Children.Clear();

        foreach (MacroDefinition macro in _settings.Macros.Where(macro => !string.IsNullOrWhiteSpace(macro.Command)))
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

        AppendStatusOutput("Disconnected", $"from {_portName}.", Brushes.Cyan);
        SetConnectionUiState(isConnected: false);
    }

    private void SetConnectionUiState(bool isConnected)
    {
        ConnectButton.Content = isConnected ? "Disconnect" : "Connect";
        SendButton.IsEnabled = isConnected;
        PortComboBox.IsEnabled = !isConnected;
        BaudComboBox.IsEnabled = !isConnected;
        RefreshButton.IsEnabled = !isConnected;
        LineEndingComboBox.IsEnabled = true;
    }

    private void RefreshPorts()
    {
        string[] ports = SerialMonitor.GetAvailablePorts();
        PortComboBox.ItemsSource = ports;
        if (ports.Length > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }
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

    private InlineCollection GetOutputInlines()
    {
        return OutputTextBlock.Inlines ??= new InlineCollection();
    }

    public async Task ShowFindWindowAsync()
    {
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

        findWindow.Show(this);
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
        _findWindow?.Close();
        _settingsWindow?.Close();
        _monitor?.Dispose();
        base.OnClosed(e);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
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

        await dialog.ShowDialog(this);
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
            SuggestedFileName = $"serial-log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt",
            SuggestedStartLocation = suggestedStartLocation,
            DefaultExtension = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text files")
                {
                    Patterns = new[] { "*.txt" }
                },
                new FilePickerFileType("Log files")
                {
                    Patterns = new[] { "*.log" }
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

    public bool ToggleTimestamps()
    {
        _timestampsEnabled = !_timestampsEnabled;
        RenderOutput();

        string state = _timestampsEnabled ? "enabled" : "disabled";
        TimestampsToggled?.Invoke(_timestampsEnabled);

        return _timestampsEnabled;
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
