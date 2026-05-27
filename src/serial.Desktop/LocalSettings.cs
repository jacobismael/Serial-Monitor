using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace serial.Desktop;

public sealed class LocalSettings
{
    public const string DefaultFontFamily = "Menlo, Consolas, monospace";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FontFamily { get; set; } = DefaultFontFamily;

    public string DefaultLogSaveLocation { get; set; } = "";

    public string StartupCommand { get; set; } = "";

    public StatusPanelSettings StatusPanel { get; set; } = new();

    public SerialPlotterSettings SerialPlotter { get; set; } = new();

    public LogicAnalyzerSettings LogicAnalyzer { get; set; } = new();

    public List<MacroDefinition> Macros { get; set; } = [];

    public List<WaveformProbeDefinition> WaveformProbes { get; set; } = DefaultWaveformProbes();

    private static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SerialMonitor");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static LocalSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new LocalSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            LocalSettings? settings = JsonSerializer.Deserialize<LocalSettings>(json, JsonOptions);

            if (settings == null)
            {
                return new LocalSettings();
            }

            if (string.IsNullOrWhiteSpace(settings.FontFamily))
            {
                settings.FontFamily = DefaultFontFamily;
            }

            settings.DefaultLogSaveLocation ??= "";
            settings.StartupCommand ??= "";
            settings.StatusPanel ??= new StatusPanelSettings();
            settings.StatusPanel.Normalize();
            settings.SerialPlotter ??= new SerialPlotterSettings();
            settings.SerialPlotter.Normalize();
            settings.LogicAnalyzer ??= new LogicAnalyzerSettings();
            settings.LogicAnalyzer.Normalize();
            settings.Macros ??= [];
            settings.WaveformProbes = settings.WaveformProbes == null
                ? DefaultWaveformProbes()
                : NormalizeWaveformProbes(settings.WaveformProbes);

            return settings;
        }
        catch
        {
            return new LocalSettings();
        }
    }

    public static void Save(LocalSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    public static List<WaveformProbeDefinition> NormalizeWaveformProbes(
        IEnumerable<WaveformProbeDefinition>? probes)
    {
        List<WaveformProbeDefinition> normalized = [];
        List<WaveformProbeDefinition> source = probes?.ToList() ?? [];

        for (int i = 0; i < source.Count; i++)
        {
            WaveformProbeDefinition probe = source[i];
            bool isEmpty = string.IsNullOrWhiteSpace(probe.Probe)
                && string.IsNullOrWhiteSpace(probe.Signal)
                && string.IsNullOrWhiteSpace(probe.Color);
            if (isEmpty)
            {
                continue;
            }

            normalized.Add(new WaveformProbeDefinition
            {
                Probe = string.IsNullOrWhiteSpace(probe.Probe) ? $"Probe {i + 1}" : probe.Probe.Trim(),
                Signal = string.IsNullOrWhiteSpace(probe.Signal) ? $"P{i + 1}" : probe.Signal.Trim(),
                Color = string.IsNullOrWhiteSpace(probe.Color) ? GetDefaultProbeColor(i) : probe.Color.Trim()
            });
        }

        return normalized;
    }

    public static List<WaveformProbeDefinition> DefaultWaveformProbes()
    {
        string[] defaultSignals = ["TXD", "RXD", "DTR", "RTS", "CTS", "DCD", "DSR", "RI"];

        return defaultSignals
            .Select((signal, index) => new WaveformProbeDefinition
            {
                Probe = $"Probe {index + 1}",
                Signal = signal,
                Color = GetDefaultProbeColor(index)
            })
            .ToList();
    }

    public static string GetDefaultProbeColor(int index)
    {
        string[] colors =
        [
            "#00D4D8",
            "#E8E8E8",
            "#7CFF68",
            "#F7D154",
            "#FF6B9A",
            "#9A7CFF",
            "#56A7FF",
            "#CFCFCF"
        ];

        return colors[index % colors.Length];
    }
}

public sealed class MacroDefinition
{
    public string Name { get; set; } = "";

    public string Command { get; set; } = "";

    public string Type { get; set; } = MacroTypes.Serial;

    public string Ending { get; set; } = MacroEndingTypes.Current;

    public bool ShowAsButton { get; set; } = true;
}

public sealed class StatusPanelSettings
{
    public string DataBits { get; set; } = "8";

    public string Parity { get; set; } = "None";

    public string StopBits { get; set; } = "1";

    public string FlowControl { get; set; } = "None";

    public void Normalize()
    {
        DataBits = string.IsNullOrWhiteSpace(DataBits) ? "8" : DataBits.Trim();
        Parity = string.IsNullOrWhiteSpace(Parity) ? "None" : Parity.Trim();
        StopBits = string.IsNullOrWhiteSpace(StopBits) ? "1" : StopBits.Trim();
        FlowControl = string.IsNullOrWhiteSpace(FlowControl) ? "None" : FlowControl.Trim();
    }
}

public sealed class SerialPlotterSettings
{
    public int MaxSamples { get; set; } = 400;

    public int VisibleSamples { get; set; } = 400;

    public bool SaveFullHistory { get; set; } = true;

    public int MaxHistorySamples { get; set; } = 100000;

    public string XAxisMode { get; set; } = "Samples";

    public string LineColor { get; set; } = "#00D4D8";

    public List<string> SeriesColors { get; set; } =
    [
        "#00D4D8",
        "#E8E8E8",
        "#7CFF68",
        "#F7D154",
        "#FF6B9A",
        "#9A7CFF",
        "#56A7FF",
        "#CFCFCF"
    ];

    public bool AutoScale { get; set; } = true;

    public double MinimumValue { get; set; } = 0;

    public double MaximumValue { get; set; } = 100;

    public bool ShowGrid { get; set; } = true;

    public bool ShowPoints { get; set; }

    public double LineThickness { get; set; } = 1.5;

    public string CsvExportMode { get; set; } = "Visible";

    public string ParserMode { get; set; } = "Auto";

    public void Normalize()
    {
        MaxSamples = Math.Clamp(MaxSamples, 10, 100000);
        VisibleSamples = Math.Clamp(VisibleSamples, 10, Math.Max(MaxSamples, VisibleSamples));
        MaxHistorySamples = Math.Clamp(MaxHistorySamples, VisibleSamples, 1000000);
        XAxisMode = XAxisMode is "Samples" or "Time" ? XAxisMode : "Samples";
        LineColor = string.IsNullOrWhiteSpace(LineColor) ? "#00D4D8" : LineColor.Trim();
        SeriesColors ??= [];
        SeriesColors = SeriesColors
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .Select(color => color.Trim())
            .ToList();
        if (SeriesColors.Count == 0)
        {
            SeriesColors.Add(LineColor);
        }

        LineThickness = Math.Clamp(LineThickness, 0.5, 6);
        CsvExportMode = CsvExportMode is "Visible" or "Full History" ? CsvExportMode : "Visible";
        ParserMode = ParserMode is "Auto" or "Single Value" or "Key-Value" or "CSV" ? ParserMode : "Auto";

        if (MaximumValue <= MinimumValue)
        {
            MaximumValue = MinimumValue + 1;
        }
    }
}

public sealed class LogicAnalyzerSettings
{
    public bool ShowFpgaStatusPanel { get; set; } = true;

    public bool ShowProtocolAnalyzer { get; set; } = true;

    public int DefaultSampleRate { get; set; } = 1000000;

    public int DefaultChannelCount { get; set; } = 8;

    public int DefaultTriggerChannel { get; set; }

    public string DefaultTriggerMode { get; set; } = "None";

    public string DefaultProtocolDecoder { get; set; } = "Auto";

    public int DefaultUartBaud { get; set; } = 115200;

    public int DefaultI2cSdaChannel { get; set; } = 5;

    public int DefaultI2cSclChannel { get; set; } = 4;

    public int DefaultSpiSclkChannel { get; set; } = 6;

    public int DefaultSpiMosiChannel { get; set; } = 7;

    public int DefaultSpiMisoChannel { get; set; } = -1;

    public int DefaultSpiCsChannel { get; set; } = -1;

    public int DefaultCanBaud { get; set; } = 500000;

    public int CaptureDepth { get; set; } = 8192;

    public double ZoomSensitivity { get; set; } = 1.08;

    public int DefaultTriggerPositionPercent { get; set; } = 50;

    public string DefaultTriggerCombineMode { get; set; } = "AND";

    public string DefaultCaptureQualificationMode { get; set; } = "Store All Samples";

    public bool EnableAutoRetriggerByDefault { get; set; }

    public string DefaultAutoCaptureLimit { get; set; } = "Infinite";

    public bool DefaultCursorSnapMode { get; set; }

    public string DefaultBusRadix { get; set; } = "Hex";

    public bool SaveDecodedFramesInCaptureFile { get; set; } = true;

    public bool SaveAnnotationsInCaptureFile { get; set; } = true;

    public void Normalize()
    {
        DefaultSampleRate = Math.Clamp(DefaultSampleRate, 1, 100000000);
        DefaultChannelCount = DefaultChannelCount == 16 ? 16 : 8;
        DefaultTriggerChannel = Math.Clamp(DefaultTriggerChannel, 0, DefaultChannelCount - 1);
        DefaultTriggerMode = DefaultTriggerMode is "None" or "Rising" or "Falling" or "High" or "Low"
            ? DefaultTriggerMode
            : "None";
        DefaultProtocolDecoder = DefaultProtocolDecoder is "Auto" or "UART" or "I2C" or "SPI" or "CAN"
            ? DefaultProtocolDecoder
            : "Auto";
        DefaultUartBaud = Math.Clamp(DefaultUartBaud, 1, 2000000);
        DefaultI2cSdaChannel = Math.Clamp(DefaultI2cSdaChannel, 0, DefaultChannelCount - 1);
        DefaultI2cSclChannel = Math.Clamp(DefaultI2cSclChannel, 0, DefaultChannelCount - 1);
        DefaultSpiSclkChannel = Math.Clamp(DefaultSpiSclkChannel, 0, DefaultChannelCount - 1);
        DefaultSpiMosiChannel = Math.Clamp(DefaultSpiMosiChannel, 0, DefaultChannelCount - 1);
        DefaultCanBaud = Math.Clamp(DefaultCanBaud, 1, 1000000);
        CaptureDepth = Math.Clamp(CaptureDepth, 1, 100000000);
        ZoomSensitivity = Math.Clamp(ZoomSensitivity, 1.01, 1.25);
        DefaultTriggerPositionPercent = Math.Clamp(DefaultTriggerPositionPercent, 0, 100);
        DefaultTriggerCombineMode = DefaultTriggerCombineMode is "AND" or "OR" ? DefaultTriggerCombineMode : "AND";
        DefaultCaptureQualificationMode = DefaultCaptureQualificationMode is "Store All Samples" or "Store When Condition True"
            ? DefaultCaptureQualificationMode
            : "Store All Samples";
        DefaultAutoCaptureLimit = DefaultAutoCaptureLimit is "Infinite" or "1" or "5" or "10" or "100"
            ? DefaultAutoCaptureLimit
            : "Infinite";
        DefaultBusRadix = DefaultBusRadix is "Binary" or "Hex" or "Unsigned" or "Signed"
            ? DefaultBusRadix
            : "Hex";
    }
}

public static class MacroTypes
{
    public const string Serial = "Serial";

    public const string Shell = "Shell";
}

public static class MacroEndingTypes
{
    public const string Current = "Current";

    public const string None = "None";

    public const string LF = "LF";

    public const string CR = "CR";

    public const string CRLF = "CRLF";
}

public sealed class WaveformProbeDefinition
{
    public string Probe { get; set; } = "";

    public string Signal { get; set; } = "";

    public string Color { get; set; } = "";
}
