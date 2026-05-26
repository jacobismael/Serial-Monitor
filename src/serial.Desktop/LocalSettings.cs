using System;
using System.Collections.Generic;
using System.IO;
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

    public List<MacroDefinition> Macros { get; set; } = [];

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
            settings.Macros ??= [];

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
}

public sealed class MacroDefinition
{
    public string Name { get; set; } = "";

    public string Command { get; set; } = "";

    public string Type { get; set; } = MacroTypes.Serial;
}

public static class MacroTypes
{
    public const string Serial = "Serial";

    public const string Shell = "Shell";
}
