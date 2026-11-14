using LiteRec.Interop;

namespace LiteRec.Audio;

/// <summary>An audio endpoint shown in the device dropdowns. An empty Id means "system default".</summary>
internal sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class AudioDevices
{
    /// <summary>Playback devices; recording one of these captures "what you hear" (WASAPI loopback).</summary>
    public static Task<List<AudioDevice>> GetPlaybackDevicesAsync() => FindAsync(CoreAudio.DataFlow.Render);

    /// <summary>Recording devices (microphones, line-in).</summary>
    public static Task<List<AudioDevice>> GetMicrophonesAsync() => FindAsync(CoreAudio.DataFlow.Capture);

    private static Task<List<AudioDevice>> FindAsync(CoreAudio.DataFlow flow) => Task.Run(() =>
    {
        try
        {
            return CoreAudio.EnumerateDevices(flow).ConvertAll(device => new AudioDevice(device.Id, device.Name));
        }
        catch (Exception)
        {
            // No audio service / no devices: the UI simply offers only "Default device".
            return [];
        }
    });
}
