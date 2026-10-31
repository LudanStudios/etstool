using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aliyun.Acs.Core;
using Aliyun.Acs.Core.Http;
using Aliyun.Acs.Core.Profile;

namespace ETSAssistant.WinUI3.Services;

public sealed record AliyunTtsResult(bool Success, string FilePath = "", string Error = "", int ChunkCount = 0);

/// <summary>
/// Creates short-lived NLS tokens from configured AccessKey credentials,
/// reuses them until shortly before their reported expiration, then calls standard TTS.
/// </summary>
public sealed class AliyunTtsService
{
    private const string Region = "cn-shanghai";
    private const string MetaDomain = "nls-meta.cn-shanghai.aliyuncs.com";
    private const string TtsEndpoint = "https://nls-gateway-cn-shanghai.aliyuncs.com/stream/v1/tts";
    private const int MaxCharactersPerRequest = 300;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static TokenCache? _tokenCache;
    private readonly string _programDirectory;

    private sealed record TokenCache(string CredentialFingerprint, string Token, DateTimeOffset ExpiresAt);

    public AliyunTtsService(string programDirectory) => _programDirectory = programDirectory;

    public async Task<AliyunTtsResult> SynthesizeToWavAsync(string text, CancellationToken cancellationToken = default)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            return new AliyunTtsResult(false, Error: "没有可合成的回答文本。先生成或提取一条口语回答。");

        AliyunTtsCredentials? credentials = AliyunTtsCredentialStore.Load();
        bool hasLocalCredentials = credentials is not null &&
            !string.IsNullOrWhiteSpace(credentials.AccessKeyId) &&
            !string.IsNullOrWhiteSpace(credentials.AccessKeySecret) &&
            !string.IsNullOrWhiteSpace(credentials.AppKey);
        if (!hasLocalCredentials)
            return new AliyunTtsResult(false, Error: "未填写有效的凭据\n无法完成指定操作。");

        IReadOnlyList<string> chunks = SplitText(text, MaxCharactersPerRequest);

        try
        {
            var waveFiles = new List<byte[]>(chunks.Count);
            foreach (string chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string token = await GetTokenAsync(credentials!, cancellationToken).ConfigureAwait(false);
                waveFiles.Add(await SynthesizeChunkAsync(credentials!.AppKey, token, chunk, cancellationToken)
                    .ConfigureAwait(false));
            }

            byte[] wav = MergeWavFiles(waveFiles);
            string outputDirectory = Path.Combine(_programDirectory, "temp", "tts-test");
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory,
                $"aliyun-tts-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(outputPath, wav, cancellationToken).ConfigureAwait(false);
            return new AliyunTtsResult(true, outputPath, ChunkCount: chunks.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new AliyunTtsResult(false, Error: "阿里云 TTS 请求超时，请检查网络后重试。");
        }
        catch (HttpRequestException ex)
        {
            return new AliyunTtsResult(false, Error: $"连接阿里云 TTS 失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new AliyunTtsResult(false, Error: ex.Message);
        }
    }

    private static async Task<string> GetTokenAsync(AliyunTtsCredentials credentials, CancellationToken cancellationToken)
    {
        string fingerprint = Fingerprint(credentials);
        await TokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenCache is { } cached && cached.CredentialFingerprint == fingerprint &&
                cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return cached.Token;

            TokenCache fetched = await Task.Run(() => RequestToken(credentials, fingerprint), cancellationToken)
                .ConfigureAwait(false);
            _tokenCache = fetched;
            return fetched.Token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static TokenCache RequestToken(AliyunTtsCredentials credentials, string fingerprint)
    {
        var profile = DefaultProfile.GetProfile(Region, credentials.AccessKeyId, credentials.AccessKeySecret);
        var client = new DefaultAcsClient(profile);
        var request = new CommonRequest
        {
            Domain = MetaDomain,
            Version = "2019-02-28",
            Action = "CreateToken",
            Method = MethodType.POST,
            Protocol = ProtocolType.HTTPS
        };

        CommonResponse response = client.GetCommonResponse(request);
        using JsonDocument document = JsonDocument.Parse(response.Data);
        if (!document.RootElement.TryGetProperty("Token", out JsonElement tokenObject) ||
            !tokenObject.TryGetProperty("Id", out JsonElement tokenId) ||
            tokenId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tokenId.GetString()) ||
            !tokenObject.TryGetProperty("ExpireTime", out JsonElement expireTime) ||
            !expireTime.TryGetInt64(out long expiry))
            throw new InvalidOperationException("阿里云未返回有效 Token 或过期时间，请检查 RAM 权限和凭证配置。");

        return new TokenCache(fingerprint, tokenId.GetString()!, DateTimeOffset.FromUnixTimeSeconds(expiry));
    }

    private static async Task<byte[]> SynthesizeChunkAsync(string appKey, string token, string text,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            appkey = appKey,
            token,
            text,
            format = "wav",
            sample_rate = 16000,
            voice = "betty",
            volume = 50,
            speech_rate = 0,
            pitch_rate = 0
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, TtsEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || !IsWave(body))
            throw new InvalidOperationException($"阿里云 TTS 返回 HTTP {(int)response.StatusCode}：{ExtractServiceError(body)}");
        return body;
    }

    private static IReadOnlyList<string> SplitText(string text, int maximumCharacters)
    {
        var chunks = new List<string>();
        while (text.Length > maximumCharacters)
        {
            int cut = text.LastIndexOfAny(['.', '!', '?', ';', ',', ' '], maximumCharacters - 1, maximumCharacters);
            if (cut < 1) cut = maximumCharacters;
            else cut++;

            string part = text[..cut].Trim();
            if (part.Length == 0)
            {
                cut = maximumCharacters;
                part = text[..cut].Trim();
            }
            chunks.Add(part);
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) chunks.Add(text);
        return chunks;
    }

    private static byte[] MergeWavFiles(IReadOnlyList<byte[]> files)
    {
        if (files.Count == 0) throw new InvalidOperationException("阿里云没有返回可用音频。");

        byte[]? format = null;
        using var audio = new MemoryStream();
        foreach (byte[] file in files)
        {
            (byte[] currentFormat, byte[] data) = ReadWavChunks(file);
            if (format is null) format = currentFormat;
            else if (!format.AsSpan().SequenceEqual(currentFormat))
                throw new InvalidOperationException("阿里云返回的 WAV 音频参数不一致，无法拼接。");
            audio.Write(data);
        }

        if (format is null) throw new InvalidOperationException("阿里云没有返回可用音频。");
        byte[] audioData = audio.ToArray();
        using var merged = new MemoryStream(44 + format.Length + audioData.Length);
        using var writer = new BinaryWriter(merged, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(4 + 8 + format.Length + (format.Length & 1) + 8 + audioData.Length + (audioData.Length & 1)));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(format.Length);
        writer.Write(format);
        if ((format.Length & 1) != 0) writer.Write((byte)0);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(audioData.Length);
        writer.Write(audioData);
        if ((audioData.Length & 1) != 0) writer.Write((byte)0);
        writer.Flush();
        return merged.ToArray();
    }

    private static (byte[] Format, byte[] Data) ReadWavChunks(byte[] file)
    {
        if (!IsWave(file)) throw new InvalidOperationException("阿里云返回的数据不是有效 WAV 文件。");
        byte[]? format = null;
        byte[]? data = null;
        int offset = 12;
        while (offset + 8 <= file.Length)
        {
            string chunkId = Encoding.ASCII.GetString(file, offset, 4);
            uint chunkSize = BitConverter.ToUInt32(file, offset + 4);
            offset += 8;
            if (chunkId == "data")
            {
                // Aliyun's WAV header can advertise more samples than the body
                // actually contains; clamp to the bytes we really received and
                // rebuild a correct header below.
                int available = Math.Max(0, file.Length - offset);
                int take = (int)Math.Min(chunkSize, (uint)available);
                byte[] audioPart = file.AsSpan(offset, take).ToArray();
                data = data is null ? audioPart : [.. data, .. audioPart];
                offset += take + (take & 1);
                if ((uint)take < chunkSize) break;
                continue;
            }
            if (chunkSize > int.MaxValue || offset + (long)chunkSize > file.Length)
                throw new InvalidOperationException("阿里云返回的 WAV 数据块长度无效。");
            if (chunkId == "fmt ") format ??= file.AsSpan(offset, (int)chunkSize).ToArray();
            offset += (int)chunkSize + ((int)chunkSize & 1);
        }
        if (format is null || data is null)
            throw new InvalidOperationException("阿里云返回的 WAV 缺少 fmt 或 data 块。");
        return (format, data);
    }

    private static bool IsWave(byte[] data) => data.Length >= 12 &&
        Encoding.ASCII.GetString(data, 0, 4) == "RIFF" &&
        Encoding.ASCII.GetString(data, 8, 4) == "WAVE";

    private static string Fingerprint(AliyunTtsCredentials credentials)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(credentials.AccessKeyId + "\0" + credentials.AccessKeySecret + "\0" + credentials.AppKey);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string ExtractServiceError(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out JsonElement message) &&
                message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                string status = document.RootElement.TryGetProperty("status", out JsonElement code)
                    ? $"状态码 {code}，" : string.Empty;
                return status + message.GetString();
            }
        }
        catch (JsonException) { }
        string text = Encoding.UTF8.GetString(body).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length == 0 ? "服务返回了空错误信息。" : text[..Math.Min(text.Length, 300)];
    }
}
