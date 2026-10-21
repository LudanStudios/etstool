using System.Diagnostics;
using System.Text;

namespace ETSAssistant.WinUI3.Services;

/// <summary>
/// Prepares a WAV for Chromium's fake microphone and builds the switches that
/// make ETS read it as microphone input.
/// </summary>
public sealed class SpeakingAudioService
{
    private readonly string _dataRoot;
    private readonly string? _etsRoot;

    public SpeakingAudioService(string dataRoot, string? etsExecutable)
    {
        _dataRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
        _etsRoot = string.IsNullOrWhiteSpace(etsExecutable)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(etsExecutable));
    }

    public string InputPath => Path.Combine(_dataRoot, "_assistant", "speaking_input.wav");

    public bool HasInput => File.Exists(InputPath);

    /// <summary>Converts any WAV to 16-bit PCM 48 kHz stereo, the format Chromium's fake capture expects.</summary>
    public async Task<string> PrepareAsync(string sourceWav)
    {
        if (!File.Exists(sourceWav)) throw new FileNotFoundException("找不到 WAV 文件。", sourceWav);
        Directory.CreateDirectory(Path.GetDirectoryName(InputPath)!);
        string temporary = InputPath + ".tmp.wav";
        try
        {
            string? ffmpeg = FindFfmpeg();
            if (ffmpeg is not null)
            {
                var start = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (string argument in new[]
                         {
                             "-y", "-i", sourceWav, "-ac", "2", "-ar", "48000", "-sample_fmt", "s16", temporary
                         })
                    start.ArgumentList.Add(argument);

                using Process process = Process.Start(start)
                    ?? throw new InvalidOperationException("无法启动 ffmpeg。");
                Task<string> error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("WAV 转换失败：" + Shorten(await error));
            }
            else
            {
                File.Copy(sourceWav, temporary, true);
            }
            File.Move(temporary, InputPath, true);
            return InputPath;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string BuildFakeMicrophoneArguments(bool loop)
    {
        string file = InputPath + (loop ? string.Empty : "%noloop");
        return "--use-fake-ui-for-media-stream --use-fake-device-for-media-stream " +
               $"--use-file-for-fake-audio-capture=\"{file}\"";
    }

    private string? FindFfmpeg()
    {
        if (_etsRoot is not null)
        {
            string bundled = Path.Combine(_etsRoot, "ffmpeg", "ffmpeg.exe");
            if (File.Exists(bundled)) return bundled;
        }
        foreach (string candidate in new[] { "ffmpeg.exe", @"C:\ffmpeg\bin\ffmpeg.exe" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static string Shorten(string value)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 300 ? value : value[^300..];
    }
}
