using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteRec.Recording;

namespace LiteRec.UI;

/// <summary>User preferences, persisted as JSON in %AppData%\LiteRec\settings.json between sessions.</summary>
internal sealed class AppSettings
{
    public Language Language { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "he" ? Language.Hebrew : Language.English;

    /// <summary>0 = full screen, 1 = window, 2 = custom region.</summary>
    public int CaptureMode { get; set; }

    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }

    public bool SystemAudioEnabled { get; set; } = true;
    public string SystemAudioDeviceId { get; set; } = string.Empty;
    public int SystemAudioVolume { get; set; } = 100;

    public bool MicrophoneEnabled { get; set; }
    public string MicrophoneDeviceId { get; set; } = string.Empty;
    public int MicrophoneVolume { get; set; } = 100;

    public int FramesPerSecond { get; set; } = 30;

    /// <summary>Maximum video height: 1080 = Full HD, 720 = HD, 480 = SD, 0 = original size.</summary>
    public int ResolutionHeight { get; set; } = 1080;

    public VideoQuality Quality { get; set; } = VideoQuality.Balanced;
    public bool CaptureCursor { get; set; } = true;
    public bool HidePanelFromRecording { get; set; } = true;

    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LiteRec");

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteRec", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings: start from defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        }
        catch (Exception)
        {
            // Preferences are a convenience; failing to save them must not disturb the user.
        }
    }
}

/// <summary>Source-generated JSON metadata (no reflection at startup).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
