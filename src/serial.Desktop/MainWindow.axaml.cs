using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using serial.Core;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace serial.Desktop;

public partial class MainWindow : Window
{
    private SerialMonitor? _monitor;
    private readonly List<LogEntry> _logEntries = [];
    private bool _timestampsEnabled;

    private string? _portName;
    private int? _baudRate;

    public MainWindow()
    {
        InitializeComponent();

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

        SetConnectionUiState(isConnected: false);

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
        if (_monitor == null)
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
        LineEndingComboBox.IsEnabled = !isConnected;
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

    private void RenderOutput()
    {
        InlineCollection outputInlines = GetOutputInlines();
        outputInlines.Clear();

        foreach (LogEntry entry in _logEntries)
        {
            AddEntryRuns(entry);
        }

        OutputScrollViewer.ScrollToEnd();
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
        if (_timestampsEnabled && entry.AllowTimestamp && !string.IsNullOrEmpty(GetEntryText(entry)))
        {
            GetOutputInlines().Add(new Run
            {
                Text = $"[{entry.Timestamp:HH:mm:ss}] ",
                Foreground = OutputTextBlock.Foreground
            });
        }

        foreach (LogSegment segment in entry.Segments)
        {
            Run run = new()
            {
                Text = segment.Text,
                Foreground = segment.Foreground ?? OutputTextBlock.Foreground,
                FontWeight = segment.Bold ? FontWeight.Bold : FontWeight.Normal
            };

            GetOutputInlines().Add(run);
        }
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

    protected override void OnClosed(EventArgs e)
    {
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

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save Serial Log",
                SuggestedFileName = $"serial-log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt",
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
            });

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
