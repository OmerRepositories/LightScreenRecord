using LiteRec.Capture;

namespace LiteRec.Recording;

/// <summary>Trade-off between file size / encoder load and picture quality (controls the bitrate).</summary>
internal enum VideoQuality
{
    SmallFile,
    Balanced,
    High,
}

/// <summary>Everything the recorder needs to know for one recording; captured from the UI when Start is pressed.</summary>
internal sealed class RecordingOptions
{
    public required CaptureTarget Target { get; init; }

    public required string OutputDirectory { get; init; }

    public int FramesPerSecond { get; init; } = 30;

    /// <summary>
    /// Maximum height of the saved video in pixels (1080 = Full HD, 720 = HD...). Larger captures are
    /// scaled down keeping their proportions; smaller ones are never enlarged. 0 = keep the original size.
    /// </summary>
    public int MaxHeight { get; init; } = 1080;

    public VideoQuality Quality { get; init; } = VideoQuality.Balanced;

    public bool CaptureCursor { get; init; } = true;

    /// <summary>Playback device to record via loopback. Null = no system audio, empty = default device.</summary>
    public string? SystemAudioDeviceId { get; init; }

    /// <summary>Microphone to record. Null = no microphone, empty = default device.</summary>
    public string? MicrophoneDeviceId { get; init; }

    public double SystemAudioGain { get; init; } = 1.0;

    public double MicrophoneGain { get; init; } = 1.0;
}
