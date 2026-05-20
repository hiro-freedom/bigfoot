using System;
using System.IO;
using System.Text.Json;

namespace bigfoot;

public sealed class AppSettings
{
    public double SilenceThreshold { get; set; } = 0.02;
    public double VerticalPositionRatio { get; set; } = 0.08;
    public bool ExcludeMyself { get; set; }
    public bool UseQuantizedPosition { get; set; } = true;
    public string ColorTheme { get; set; } = "Default";
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "bigfoot");

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppSettings();
            }

            var text = File.ReadAllText(ConfigPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(text);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var text = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(ConfigPath, text);
        }
        catch
        {
            // Ignore write failures to avoid crashing UI loop.
        }
    }
}
