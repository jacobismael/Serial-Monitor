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

    public int VisibleSamples { get; set; } = 160;

    public string LineColor { get; set; } = "#00D4D8";

    public bool AutoScale { get; set; } = true;

    public double MinimumValue { get; set; } = 0;

    public double MaximumValue { get; set; } = 100;

    public void Normalize()
    {
        MaxSamples = Math.Clamp(MaxSamples, 10, 10000);
        VisibleSamples = Math.Clamp(VisibleSamples, 10, MaxSamples);
        LineColor = string.IsNullOrWhiteSpace(LineColor) ? "#00D4D8" : LineColor.Trim();

        if (MaximumValue <= MinimumValue)
        {
            MaximumValue = MinimumValue + 1;
        }
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
