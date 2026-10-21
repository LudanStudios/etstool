using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ETSAssistant.WinUI3.Services;

public sealed record VirtualCableDevice(string Name, DeviceInformation Device);

/// <summary>
/// Plays WAV files into a virtual cable's playback endpoint (CABLE Input).
/// ETS must use the matching recording endpoint (CABLE Output) as its mic.
/// </summary>
public sealed class VirtualCableService
{
    private MediaPlayer? _player;
    private MediaPlayer? _previewPlayer;

    public VirtualCableDevice? Current { get; private set; }
    public bool IsAvailable => Current is not null;
    public event Action? PlaybackEnded;

    public async Task<VirtualCableDevice?> RefreshAsync()
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
        DeviceInformation? match = devices
            .Select(device => (Device: device, Score: Score(device.Name)))
            .Where(item => item.Score < 99)
            .OrderBy(item => item.Score)
            .Select(item => item.Device)
            .FirstOrDefault();
        Current = match is null ? null : new VirtualCableDevice(match.Name, match);
        return Current;
    }

    public void Play(string wavPath)
    {
        if (Current is null)
            throw new InvalidOperationException("没有检测到虚拟声卡（CABLE Input / VoiceMeeter Input）。");
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("找不到要播放的 WAV。", wavPath);

        if (_player is null)
        {
            _player = new MediaPlayer { AutoPlay = false };
            _player.MediaEnded += (_, _) => PlaybackEnded?.Invoke();
        }
        _player.AudioDevice = Current.Device;
        _player.Source = MediaSource.CreateFromUri(new Uri(wavPath));
        _player.Play();
    }

    public void Stop() => _player?.Pause();

    /// <summary>Plays a WAV through the normal speakers so the operator can hear it.</summary>
    public void PlayPreview(string wavPath)
    {
        if (!File.Exists(wavPath)) throw new FileNotFoundException("找不到要试听的 WAV。", wavPath);
        _previewPlayer ??= new MediaPlayer { AutoPlay = false };
        _previewPlayer.Source = MediaSource.CreateFromUri(new Uri(wavPath));
        _previewPlayer.Play();
    }

    public void StopPreview() => _previewPlayer?.Pause();

    private static int Score(string name)
    {
        if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("16 Ch", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Output", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("CABLE In", StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)) return 5;
        return 99;
    }
}
