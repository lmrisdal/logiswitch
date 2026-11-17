using System.Text.Json;

namespace LogiSwitch;

/// <summary>User settings, stored as JSON in %APPDATA%\logiswitch\config.json.</summary>
internal sealed class Config
{
    /// <summary>Substring that identifies the headset's audio endpoints (output and mic).</summary>
    public string HeadsetNameMatch { get; set; } = "G522";

    /// <summary>Output endpoint to use while the headset is off. Takes precedence over the name match.</summary>
    public string? FallbackOutputId { get; set; }

    /// <summary>Used when <see cref="FallbackOutputId"/> is unset or not active.</summary>
    public string FallbackOutputNameMatch { get; set; } = "NuForce";

    /// <summary>Whether to move the default microphone as well.</summary>
    public bool SwitchInput { get; set; } = true;

    /// <summary>Mic to use while the headset is off. Null = first active mic that isn't the headset.</summary>
    public string? FallbackInputId { get; set; }

    /// <summary>Wait this long after power-off before switching, to ride out range drop-outs.</summary>
    public int OffDelayMs { get; set; } = 2000;

    /// <summary>
    /// Only switch away from the headset if it is currently the default, and only switch back
    /// if the current default is the fallback. Leaves manual choices alone.
    /// </summary>
    public bool RespectManualChoice { get; set; } = true;

    /// <summary>How often to re-ask the receiver for the headset state, as a safety net.</summary>
    public int StatusPollSeconds { get; set; } = 60;

    public bool Paused { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "logiswitch", "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOptions) ?? new Config();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {FilePath}, using defaults", ex);
            return new Config();
        }

        var config = new Config();
        config.Save();
        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write {FilePath}", ex);
        }
    }
}
