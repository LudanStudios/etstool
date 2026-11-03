using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ETSAssistant.WinUI3.Services;

public sealed record DeepSeekResult(bool Success, string Text, string Error = "", int StatusCode = 0);

/// <summary>
/// Minimal DeepSeek chat client for speaking-question tests. Every request sends
/// the question stem plus the llm_stt.md attachment (as the system prompt).
/// </summary>
public sealed class DeepSeekService
{
    public const string Model = "deepseek-flash";
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly string _appRoot;

    public DeepSeekService(string appRoot) => _appRoot = appRoot;

    public string? FindAttachment()
    {
        foreach (string path in AttachmentCandidates())
            if (File.Exists(path)) return path;
        return null;
    }

    public async Task<DeepSeekResult> AskSpeakingAsync(string stem, CancellationToken cancellationToken = default)
    {
        stem = (stem ?? string.Empty).Trim();
        if (stem.Length == 0) return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Translate("deepseek.empty_prompt"));

        string apiKey = ApiKeyStore.Load();
        if (apiKey.Length == 0)
            return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Translate("common.invalid_credentials_error"));

        string? attachment = FindAttachment();
        if (attachment is null)
            return new DeepSeekResult(false, string.Empty,
                LocalizationService.Current.Translate("deepseek.attachment_missing"));

        string instructions;
        try
        {
            instructions = await File.ReadAllTextAsync(attachment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Format("deepseek.attachment_read_failed",
                ("file", Path.GetFileName(attachment)), ("detail", ex.Message)));
        }

        string payload = JsonSerializer.Serialize(new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = stem }
            },
            stream = false,
            temperature = 0.7
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new DeepSeekResult(false, string.Empty,
                    LocalizationService.Current.Format("deepseek.http_error", ("status", (int)response.StatusCode),
                        ("detail", ExtractError(body))), (int)response.StatusCode);

            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Format("deepseek.invalid_response",
                    ("detail", Shorten(body))), 200);

            JsonElement message = choices[0];
            if (message.TryGetProperty("message", out JsonElement messageObject) &&
                messageObject.TryGetProperty("content", out JsonElement content) &&
                content.ValueKind == JsonValueKind.String)
            {
                string text = (content.GetString() ?? string.Empty).Trim();
                if (text.Length > 0) return new DeepSeekResult(true, text);
            }
            return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Translate("deepseek.empty_response"), 200);
        }
        catch (TaskCanceledException)
        {
            return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Translate("deepseek.timeout"));
        }
        catch (HttpRequestException ex)
        {
            return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Format("deepseek.network_error", ("detail", ex.Message)));
        }
        catch (JsonException)
        {
            return new DeepSeekResult(false, string.Empty, LocalizationService.Current.Translate("deepseek.invalid_json"));
        }
    }

    private IEnumerable<string> AttachmentCandidates()
    {
        yield return Path.Combine(_appRoot, "llm_stt.md");
        yield return Path.Combine(AppContext.BaseDirectory, "llm_stt.md");
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "llm_stt.md");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "llm_stt.md");
    }

    private static string ExtractError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out JsonElement message) &&
                message.ValueKind == JsonValueKind.String)
            {
                string text = message.GetString() ?? string.Empty;
                if (text.Length > 0) return text;
            }
        }
        catch (JsonException) { }
        return Shorten(body);
    }

    private static string Shorten(string value)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 400 ? value : value[..400] + "…";
    }
}
