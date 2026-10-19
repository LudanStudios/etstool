using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ETSAssistant.WinUI3.Services;

public enum AnswerResetStatus { Applied, NotResponding, Failed }

public sealed record AnswerResetResult(AnswerResetStatus Status, string Message);

/// <summary>
/// Asks the background answer worker to forget the current attempt and answer
/// again from the question the user is looking at. The worker watches
/// <c>%APPDATA%\ETS\_assistant\reset_request.json</c> and replies with
/// <c>reset_ack.json</c>, so no extra port or named pipe is needed.
/// </summary>
public sealed class AnswerResetService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private readonly string _controlRoot;
    private readonly string _requestPath;
    private readonly string _ackPath;

    public AnswerResetService(string dataRoot)
    {
        _controlRoot = Path.Combine(Path.GetFullPath(dataRoot), "_assistant");
        _requestPath = Path.Combine(_controlRoot, "reset_request.json");
        _ackPath = Path.Combine(_controlRoot, "reset_ack.json");
    }

    public async Task<AnswerResetResult> RequestAsync(string packId)
    {
        try
        {
            Directory.CreateDirectory(_controlRoot);
            long requestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string payload = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                requested_at = requestedAt,
                pack_id = packId,
                source = "winui"
            });
            await WriteRequestAsync(payload).ConfigureAwait(false);

            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < RequestTimeout)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (!TryReadAck(out long acknowledgedAt, out string status) || acknowledgedAt != requestedAt)
                    continue;
                return status == "applied"
                    ? new AnswerResetResult(AnswerResetStatus.Applied,
                        "已重置答题进度，助手正在从当前页面重新作答。")
                    : new AnswerResetResult(AnswerResetStatus.Failed, $"后台没有接受重置请求（{status}）。");
            }

            TryDelete(_requestPath);
            return new AnswerResetResult(AnswerResetStatus.NotResponding,
                "重置请求已发送，但后台答题进程没有响应。请确认 ETS 正在运行，再试一次。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AnswerResetResult(AnswerResetStatus.Failed, $"无法发送重置请求：{ex.Message}");
        }
    }

    private async Task WriteRequestAsync(string payload)
    {
        string temporary = _requestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, payload, new UTF8Encoding(false)).ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, _requestPath, true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private bool TryReadAck(out long requestedAt, out string status)
    {
        requestedAt = 0;
        status = string.Empty;
        try
        {
            if (!File.Exists(_ackPath)) return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(_ackPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("requested_at", out JsonElement token) ||
                token.ValueKind != JsonValueKind.Number || !token.TryGetInt64(out requestedAt))
                return false;
            status = root.TryGetProperty("status", out JsonElement value) &&
                     value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
