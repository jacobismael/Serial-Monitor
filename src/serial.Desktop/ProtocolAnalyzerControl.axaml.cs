using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using serial.Core;

namespace serial.Desktop;

public partial class ProtocolAnalyzerControl : UserControl
{
    private readonly ProtocolAnalyzerService _service = new();
    private LogicCapture? _capture;
    private IReadOnlyList<ProtocolFrame> _frames = [];
    private IReadOnlyList<string> _signalLabels = [];
    private string _decodeStatus = "No capture loaded";
    private DateTime? _lastDecodeAt;

    public event Action<IReadOnlyList<ProtocolFrame>>? FramesChanged;
    public event Action<ProtocolFrame>? FrameSelected;
    public event Action? StatusChanged;

    public string SelectedProtocol => ProtocolComboBox.SelectedItem as string ?? "Auto";

    public string DecodeStatus => _decodeStatus;

    public int FrameCount => _frames.Count;

    public int ErrorCount => _frames.Count(frame => frame.HasError);

    public DateTime? LastDecodeAt => _lastDecodeAt;

    public ProtocolAnalyzerControl()
    {
        InitializeComponent();

        ProtocolComboBox.ItemsSource = new[] { "Auto", "UART", "I2C", "SPI", "CAN" };
        ProtocolComboBox.SelectedItem = "Auto";
        ProtocolComboBox.SelectionChanged += (_, _) => StatusChanged?.Invoke();
        UartBaudComboBox.ItemsSource = new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
        UartBaudComboBox.SelectedItem = 115200;
        CanBitrateComboBox.ItemsSource = new[] { 125000, 250000, 500000, 1000000 };
        CanBitrateComboBox.SelectedItem = 500000;

        ConfigureChannelSelectors(8);

        AutoDetectButton.Click += (_, _) => AutoDetect();
        DecodeButton.Click += (_, _) => Decode();
        ExportDecodedCsvButton.Click += async (_, _) => await ExportDecodedCsvAsync();
        FrameListBox.SelectionChanged += (_, _) =>
        {
            if (FrameListBox.SelectedItem is ProtocolFrameListItem item)
            {
                FrameSelected?.Invoke(item.Frame);
            }
        };
    }

    public void SetCapture(LogicCapture? capture)
    {
        _capture = capture;
        _frames = [];
        FrameListBox.ItemsSource = null;
        FramesChanged?.Invoke(_frames);

        int channelCount = capture?.ChannelCount ?? 8;
        ConfigureChannelSelectors(channelCount);
        DetectionTextBlock.Text = capture == null
            ? "No capture loaded."
            : $"Capture ready: {capture.SampleCount} samples at {capture.SampleRateHz} Hz.";
        _decodeStatus = DetectionTextBlock.Text;
        _lastDecodeAt = null;
        StatusChanged?.Invoke();
    }

    public void SetSignalLabels(IEnumerable<string>? labels)
    {
        _signalLabels = labels?.ToArray() ?? [];
        ConfigureChannelSelectors(_capture?.ChannelCount ?? _signalLabels.Count);
    }

    public void ApplySettings(LogicAnalyzerSettings settings)
    {
        settings.Normalize();
        ProtocolComboBox.SelectedItem = settings.DefaultProtocolDecoder;
        UartBaudComboBox.SelectedItem = settings.DefaultUartBaud;
        CanBitrateComboBox.SelectedItem = settings.DefaultCanBaud;
        RxChannelComboBox.SelectedItem = GetChannelOption(Math.Clamp(settings.DefaultTriggerChannel, 0, settings.DefaultChannelCount - 1));
        SdaChannelComboBox.SelectedItem = GetChannelOption(Math.Clamp(settings.DefaultI2cSdaChannel, 0, settings.DefaultChannelCount - 1));
        SclChannelComboBox.SelectedItem = GetChannelOption(Math.Clamp(settings.DefaultI2cSclChannel, 0, settings.DefaultChannelCount - 1));
        SclkChannelComboBox.SelectedItem = GetChannelOption(Math.Clamp(settings.DefaultSpiSclkChannel, 0, settings.DefaultChannelCount - 1));
        MosiChannelComboBox.SelectedItem = GetChannelOption(Math.Clamp(settings.DefaultSpiMosiChannel, 0, settings.DefaultChannelCount - 1));
        MisoChannelComboBox.SelectedItem = settings.DefaultSpiMisoChannel < 0 ? "None" : GetChannelOption(settings.DefaultSpiMisoChannel);
        CsChannelComboBox.SelectedItem = settings.DefaultSpiCsChannel < 0 ? "None" : GetChannelOption(settings.DefaultSpiCsChannel);
        StatusChanged?.Invoke();
    }

    private void ConfigureChannelSelectors(int channelCount)
    {
        channelCount = Math.Max(1, channelCount);
        string[] channels = Enumerable.Range(0, channelCount)
            .Select(GetChannelOption)
            .ToArray();
        string[] optionalChannels = channels
            .Prepend("None")
            .ToArray();

        SetItems(RxChannelComboBox, channels, GetChannelOption(0));
        SetItems(SdaChannelComboBox, channels, GetChannelOption(Math.Min(5, channelCount - 1)));
        SetItems(SclChannelComboBox, channels, GetChannelOption(Math.Min(4, channelCount - 1)));
        SetItems(SclkChannelComboBox, channels, GetChannelOption(Math.Min(6, channelCount - 1)));
        SetItems(MosiChannelComboBox, channels, GetChannelOption(Math.Min(7, channelCount - 1)));
        SetItems(MisoChannelComboBox, optionalChannels, "None");
        SetItems(CsChannelComboBox, optionalChannels, "None");
    }

    private static void SetItems<T>(ComboBox comboBox, IEnumerable<T> items, T fallback)
    {
        object? selected = comboBox.SelectedItem;
        T[] values = items.ToArray();
        comboBox.ItemsSource = values;
        comboBox.SelectedItem = selected is T typed && values.Contains(typed) ? typed : fallback;
    }

    private void AutoDetect()
    {
        if (_capture == null)
        {
            DetectionTextBlock.Text = "No capture loaded.";
            _decodeStatus = DetectionTextBlock.Text;
            StatusChanged?.Invoke();
            return;
        }

        IReadOnlyList<ProtocolDetectionResult> results = _service.DetectAll(_capture);
        ProtocolDetectionResult? best = results.FirstOrDefault();
        if (best == null)
        {
            DetectionTextBlock.Text = "No protocol candidates found.";
            _decodeStatus = DetectionTextBlock.Text;
            StatusChanged?.Invoke();
            return;
        }

        ProtocolComboBox.SelectedItem = best.ProtocolName;
        ApplyDetectionToUi(best);
        DetectionTextBlock.Text = $"{best.ProtocolName} confidence {best.Confidence:0.00}. {best.Notes}";
        _decodeStatus = DetectionTextBlock.Text;
        StatusChanged?.Invoke();
    }

    private void Decode()
    {
        if (_capture == null)
        {
            DetectionTextBlock.Text = "No capture loaded.";
            _decodeStatus = DetectionTextBlock.Text;
            StatusChanged?.Invoke();
            return;
        }

        string protocol = ProtocolComboBox.SelectedItem as string ?? "Auto";
        ProtocolDecoderOptions options = CreateOptions();
        _frames = _service.Decode(_capture, protocol, options);
        FrameListBox.ItemsSource = _frames
            .Select(frame => new ProtocolFrameListItem(frame))
            .ToArray();

        DetectionTextBlock.Text = _frames.Count == 0
            ? "No frames decoded with the selected settings."
            : $"Decoded {_frames.Count} frame(s).";
        _decodeStatus = DetectionTextBlock.Text;
        _lastDecodeAt = DateTime.Now;
        FramesChanged?.Invoke(_frames);
        StatusChanged?.Invoke();
    }

    private ProtocolDecoderOptions CreateOptions()
    {
        return new ProtocolDecoderOptions
        {
            Protocol = ProtocolComboBox.SelectedItem as string ?? "Auto",
            RxChannel = ReadChannel(RxChannelComboBox, 0),
            BaudRate = UartBaudComboBox.SelectedItem is int baudRate ? baudRate : 115200,
            SdaChannel = ReadChannel(SdaChannelComboBox, 5),
            SclChannel = ReadChannel(SclChannelComboBox, 4),
            SclkChannel = ReadChannel(SclkChannelComboBox, 6),
            MosiChannel = ReadChannel(MosiChannelComboBox, 7),
            MisoChannel = ReadOptionalChannel(MisoChannelComboBox),
            CsChannel = ReadOptionalChannel(CsChannelComboBox),
            CanRxChannel = ReadChannel(RxChannelComboBox, 0),
            CanBitrate = CanBitrateComboBox.SelectedItem is int canBitrate ? canBitrate : 500000
        };
    }

    private void ApplyDetectionToUi(ProtocolDetectionResult detection)
    {
        if (detection.SuggestedBaudRate.HasValue)
        {
            UartBaudComboBox.SelectedItem = detection.SuggestedBaudRate.Value;
            CanBitrateComboBox.SelectedItem = detection.SuggestedBaudRate.Value;
        }

        if (detection.SuggestedChannels.TryGetValue("RX", out int rx))
        {
            RxChannelComboBox.SelectedItem = GetChannelOption(rx);
        }

        if (detection.SuggestedChannels.TryGetValue("SDA", out int sda))
        {
            SdaChannelComboBox.SelectedItem = GetChannelOption(sda);
        }

        if (detection.SuggestedChannels.TryGetValue("SCL", out int scl))
        {
            SclChannelComboBox.SelectedItem = GetChannelOption(scl);
        }

        if (detection.SuggestedChannels.TryGetValue("SCLK", out int sclk))
        {
            SclkChannelComboBox.SelectedItem = GetChannelOption(sclk);
        }

        if (detection.SuggestedChannels.TryGetValue("MOSI", out int mosi))
        {
            MosiChannelComboBox.SelectedItem = GetChannelOption(mosi);
        }
    }

    private string GetChannelOption(int channel)
    {
        return channel >= 0 && channel < _signalLabels.Count
            ? _signalLabels[channel]
            : $"CH{channel}";
    }

    private static int ReadChannel(ComboBox comboBox, int fallback)
    {
        if (comboBox.SelectedItem is int channel)
        {
            return channel;
        }

        if (comboBox.SelectedItem is string selected)
        {
            int chIndex = selected.IndexOf("CH", StringComparison.OrdinalIgnoreCase);
            if (chIndex >= 0)
            {
                int start = chIndex + 2;
                int length = 0;
                while (start + length < selected.Length && char.IsDigit(selected[start + length]))
                {
                    length++;
                }

                if (length > 0 && int.TryParse(selected.Substring(start, length), out int parsedChannel))
                {
                    return parsedChannel;
                }
            }

            if (int.TryParse(selected, out int numericChannel))
            {
                return numericChannel;
            }
        }

        return fallback;
    }

    private static int ReadOptionalChannel(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is string selected && string.Equals(selected, "None", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return ReadChannel(comboBox, -1);
    }

    public async System.Threading.Tasks.Task ExportDecodedCsvAsync()
    {
        if (_frames.Count == 0)
        {
            DetectionTextBlock.Text = "No decoded frames to export.";
            _decodeStatus = DetectionTextBlock.Text;
            StatusChanged?.Invoke();
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        FilePickerSaveOptions options = new()
        {
            Title = "Export Decoded Protocol Frames",
            SuggestedFileName = $"logicom-decoded-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV files")
                {
                    Patterns = ["*.csv"]
                }
            ]
        };

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        if (file == null)
        {
            return;
        }

        await using Stream stream = await file.OpenWriteAsync();
        using StreamWriter writer = new(stream);
        await writer.WriteAsync(ProtocolAnalyzerService.ExportFramesCsv(_frames));
        DetectionTextBlock.Text = $"Exported {file.Name}.";
        _decodeStatus = DetectionTextBlock.Text;
        StatusChanged?.Invoke();
    }

    private sealed class ProtocolFrameListItem(ProtocolFrame frame)
    {
        public ProtocolFrame Frame { get; } = frame;

        public override string ToString()
        {
            string error = Frame.HasError ? $"  ERROR: {Frame.ErrorMessage}" : "";
            return $"{Frame.StartTimeSeconds:0.000000000}s  {Frame.Protocol}  CH[{Frame.ChannelText}]  {Frame.DecodedText}  {Frame.HexText}{error}";
        }
    }
}
