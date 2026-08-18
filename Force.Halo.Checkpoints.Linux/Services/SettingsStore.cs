using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Force.Halo.Checkpoints.Linux.Services;

/// Source-generated serialization for the settings file.
///
/// This isn't a micro-optimisation, it's load-bearing: the Linux build publishes with
/// NativeAOT, and reflection-based JSON does not survive that. It fails silently, so what
/// you actually see is the program forgetting your hotkey bindings every launch rather
/// than throwing anything. Keep every type that hits the settings file listed here.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HotkeySettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

internal static class SettingsStore
{
    private const string FileName = "settings.json";
    private const string AppFolderName = "force-halo-checkpoints";

    public static HotkeySettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return HotkeySettings.Empty;
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.HotkeySettings) ?? HotkeySettings.Empty;
        }
        catch
        {
            return HotkeySettings.Empty;
        }
    }

    public static void Save(HotkeySettings settings)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.Note))
            {
                settings = settings with
                {
                    Note = "To re-enable X11 warnings, set the SuppressX11* flags to false."
                };
            }

            string path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.HotkeySettings);
            File.WriteAllText(path, json);
        }
        catch
        {
            // If we can't save settings, keep the app functional.
        }
    }

    private static string GetSettingsPath()
    {
        string configRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(configRoot, AppFolderName, FileName);
    }
}
