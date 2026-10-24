using System.Buffers.Binary;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ETSAssistant.WinUI3.Services;

public sealed record WorkerTextResult(string Text, int RemainingToday);
public sealed record WorkerPaperAnalysisResult(string Markdown, int RemainingToday);
public sealed record WorkerAudioResult(IReadOnlyList<byte[]> Chunks, int RemainingToday);
public sealed record WorkerQuotaCounter(int Used, int Limit, int Remaining);
public sealed record WorkerServiceUsage(WorkerQuotaCounter Device, WorkerQuotaCounter Ip);
public sealed record WorkerUsageSnapshot(
    WorkerServiceUsage Listening,
    WorkerServiceUsage Speaking,
    WorkerServiceUsage Analysis,
    WorkerServiceUsage Tts,
    DateTimeOffset CheckedAt,
    DateTimeOffset ResetAt);

/// <summary>Calls the built-in ETS service without exposing its provider credentials to the client.</summary>
public static class FreeServiceWorkerClient
{
    private const string WorkerUrl = "https://ets.azursmelt.us.ci";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly string DeviceId = CreateDeviceId();

    public static async Task<WorkerUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, WorkerUrl + "/v1/usage");
        request.Headers.TryAddWithoutValidation("X-Device-Id", DeviceId);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response, bytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        JsonElement services = root.GetProperty("services");
        return new WorkerUsageSnapshot(
            ReadServiceUsage(services.GetProperty("listening")),
            ReadServiceUsage(services.GetProperty("speaking")),
            ReadServiceUsage(services.GetProperty("analysis")),
            ReadServiceUsage(services.GetProperty("tts")),
            ReadTimestamp(root, "checked_at"),
            ReadTimestamp(root, "reset_at"));
    }

    public static async Task<WorkerTextResult> GenerateSpeakingAnswerAsync(
        string stem, string instructions, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await PostJsonAsync("/v1/speaking/answer", new
        {
            request_id = Guid.NewGuid().ToString("N"),
            stem,
            instructions
        }, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, bytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        string text = document.RootElement.TryGetProperty("text", out JsonElement value)
            ? value.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Worker 没有返回口语回答。");
        int remaining = document.RootElement.TryGetProperty("remaining_today", out JsonElement quota) && quota.TryGetInt32(out int parsed)
            ? parsed : -1;
        return new WorkerTextResult(text.Trim(), remaining);
    }

    public static async Task<WorkerPaperAnalysisResult> AnalyzePaperAsync(
        string title, IReadOnlyList<string> batches, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await PostJsonAsync("/v1/paper-analysis", new
        {
            request_id = Guid.NewGuid().ToString("N"),
            title,
            batches
        }, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, bytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        string markdown = document.RootElement.TryGetProperty("markdown", out JsonElement value)
            ? value.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
            throw new InvalidOperationException("Worker 没有返回试卷解析内容。");
        int remaining = document.RootElement.TryGetProperty("remaining_today", out JsonElement quota) && quota.TryGetInt32(out int parsed)
            ? parsed : -1;
        return new WorkerPaperAnalysisResult(markdown.Trim(), remaining);
    }

    public static async Task<WorkerAudioResult> SynthesizeTtsAsync(
        IReadOnlyList<string> chunks, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await PostJsonAsync("/v1/tts/synthesize", new
        {
            request_id = Guid.NewGuid().ToString("N"),
            texts = chunks
        }, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, bytes);
        int remaining = response.Headers.TryGetValues("x-remaining-today", out IEnumerable<string>? values) &&
            int.TryParse(values.FirstOrDefault(), out int parsed) ? parsed : -1;
        return new WorkerAudioResult(ParseAudioEnvelope(bytes), remaining);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        string path, object payload, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, WorkerUrl + path);
        request.Headers.TryAddWithoutValidation("X-Device-Id", DeviceId);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void EnsureSuccess(HttpResponseMessage response, byte[] body)
    {
        if (response.IsSuccessStatusCode) return;
        string fallback = Encoding.UTF8.GetString(body).Replace('\r', ' ').Replace('\n', ' ').Trim();
        string message = fallback.Length == 0 ? $"Worker 返回 HTTP {(int)response.StatusCode}。" : fallback;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out JsonElement value) &&
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                message = value.GetString()!;
        }
        catch (JsonException) { }
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static WorkerServiceUsage ReadServiceUsage(JsonElement service) => new(
        ReadCounter(service.GetProperty("device")),
        ReadCounter(service.GetProperty("ip")));

    private static WorkerQuotaCounter ReadCounter(JsonElement counter) => new(
        counter.GetProperty("used").GetInt32(),
        counter.GetProperty("limit").GetInt32(),
        counter.GetProperty("remaining").GetInt32());

    private static DateTimeOffset ReadTimestamp(JsonElement root, string property) =>
        root.GetProperty(property).ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(root.GetProperty(property).GetString(), out DateTimeOffset value)
            ? value : throw new InvalidOperationException("Worker 返回的用量时间无效。");

    private static IReadOnlyList<byte[]> ParseAudioEnvelope(byte[] bytes)
    {
        if (bytes.Length < 6 || Encoding.ASCII.GetString(bytes, 0, 4) != "ETSW")
            throw new InvalidOperationException("Worker 返回的音频包头无效。");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        if (count is < 1 or > 20) throw new InvalidOperationException("Worker 返回的音频段数无效。");
        var chunks = new List<byte[]>(count);
        int offset = 6;
        for (int index = 0; index < count; index++)
        {
            if (offset + 4 > bytes.Length) throw new InvalidOperationException("Worker 返回的音频包已截断。");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (length < 12 || length > int.MaxValue || offset + (long)length > bytes.Length)
                throw new InvalidOperationException("Worker 返回的 WAV 长度无效。");
            byte[] chunk = bytes.AsSpan(offset, (int)length).ToArray();
            if (Encoding.ASCII.GetString(chunk, 0, 4) != "RIFF" || Encoding.ASCII.GetString(chunk, 8, 4) != "WAVE")
                throw new InvalidOperationException("Worker 返回了无效的 WAV 音频。");
            chunks.Add(chunk);
            offset += (int)length;
        }
        if (offset != bytes.Length) throw new InvalidOperationException("Worker 返回的音频包包含多余数据。");
        return chunks;
    }

    private static string CreateDeviceId()
    {
        string machineGuid = string.Empty;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            machineGuid = Convert.ToString(key?.GetValue("MachineGuid"))?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        string identity = machineGuid.Length > 0 ? machineGuid : $"{Environment.UserDomainName}:{Environment.MachineName}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"ets-free-listening-v1:{identity}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
