using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ETSAssistant.WinUI3.Services;

public sealed class PaperAnalysisService
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<string> GenerateAsync(PaperEntry paper, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string apiKey = ApiKeyStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("未填写有效的凭据\n无法完成指定操作。");

        IReadOnlyList<string> batches = ReadQuestionBatches(paper.FolderPath);
        if (batches.Count == 0) throw new InvalidDataException("试卷中没有可发送的题干与标准答案。");

        var output = new StringBuilder($"# {paper.Title}：逐题解析\n\n");
        for (int index = 0; index < batches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在请求 DeepSeek（第 {index + 1}/{batches.Count} 组）…");
            string answer = await AskAsync(apiKey, paper.Title, batches[index], cancellationToken);
            if (index > 0) output.AppendLine().AppendLine("---").AppendLine();
            output.Append(answer.Trim()).AppendLine();
        }
        return output.ToString().Trim();
    }

    private static IReadOnlyList<string> ReadQuestionBatches(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("本机试卷目录不存在。");
        var items = new List<string>();
        foreach (string contentDirectory in Directory.EnumerateDirectories(folder, "content_*")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string path = Path.Combine(contentDirectory, "content.json");
            if (!File.Exists(path)) continue;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("info", out JsonElement info) ||
                info.ValueKind != JsonValueKind.Object) continue;
            string structure = GetString(root, "structure_type");
            bool oralContent = IsOralStructure(structure);
            if (!info.TryGetProperty("xtlist", out JsonElement questions) ||
                questions.ValueKind != JsonValueKind.Array)
            {
                if (oralContent && TryFindPrompt(info, out string oralStem))
                {
                    var oralItem = new Dictionary<string, object?>
                    {
                        ["题号"] = items.Count + 1,
                        ["题型"] = "口语",
                        ["口语题干"] = oralStem
                    };
                    items.Add(JsonSerializer.Serialize(oralItem));
                }
                continue;
            }
            int number = 0;
            foreach (JsonElement question in questions.EnumerateArray())
            {
                if (question.ValueKind != JsonValueKind.Object) continue;
                number++;
                var projected = new Dictionary<string, object?> { ["题号"] = number };
                foreach (JsonProperty property in question.EnumerateObject())
                {
                    string key = property.Name;
                    if (!IsQuestionField(key)) continue;
                    projected[key] = ProjectValue(property.Value);
                }
                bool hasOralPrompt = oralContent
                    ? TryFindPrompt(question, out string oralStem)
                    : TryFindExplicitOralPrompt(question, out oralStem);
                if (hasOralPrompt) projected["口语题干"] = oralStem;
                string json = JsonSerializer.Serialize(projected);
                if (json.Length > 0) items.Add(json);
            }
        }

        const int questionsPerBatch = 15;
        var batches = new List<string>();
        for (int offset = 0; offset < items.Count; offset += questionsPerBatch)
            batches.Add(string.Join(Environment.NewLine,
                items.Skip(offset).Take(questionsPerBatch).Select((item, index) => $"{offset + index + 1}. {item}")));
        return batches;
    }

    private static bool IsOralStructure(string structure)
    {
        string value = structure.ToLowerInvariant();
        return value.Contains("collector.read", StringComparison.Ordinal) ||
               value.Contains("collector.dialogue", StringComparison.Ordinal) ||
               value.Contains("collector.picture", StringComparison.Ordinal) ||
               value.Contains("speaking", StringComparison.Ordinal) ||
               value.Contains("oral", StringComparison.Ordinal) ||
               value.Contains("recite", StringComparison.Ordinal);
    }

    private static bool TryFindExplicitOralPrompt(JsonElement element, out string prompt)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string key = property.Name.ToLowerInvariant();
                if ((key.Contains("oral", StringComparison.Ordinal) ||
                     key.Contains("speak", StringComparison.Ordinal) ||
                     key.Contains("口语", StringComparison.Ordinal) ||
                     key.Contains("朗读", StringComparison.Ordinal) || key == "xt_ky") &&
                    TryGetText(property.Value, out prompt)) return true;
            }
            foreach (JsonProperty property in element.EnumerateObject())
                if (TryFindExplicitOralPrompt(property.Value, out prompt)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                if (TryFindExplicitOralPrompt(child, out prompt)) return true;
        }
        prompt = string.Empty;
        return false;
    }

    private static bool TryFindPrompt(JsonElement element, out string prompt)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string[] promptFields = ["prompt", "instruction", "question", "stem", "xt_nr", "xt_tm", "text", "content", "passage"];
            foreach (string field in promptFields)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(property.Name, "口语题干", StringComparison.Ordinal)) continue;
                    if (TryGetText(property.Value, out prompt)) return true;
                }
            }
            foreach (JsonProperty property in element.EnumerateObject())
                if (TryFindPrompt(property.Value, out prompt)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                if (TryFindPrompt(child, out prompt)) return true;
        }
        prompt = string.Empty;
        return false;
    }

    private static bool TryGetText(JsonElement element, out string text)
    {
        if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            text = element.GetString()!.Trim();
            return true;
        }
        text = string.Empty;
        return false;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static bool IsQuestionField(string key)
    {
        string normalized = key.ToLowerInvariant();
        if (normalized.Contains("audio") || normalized.Contains("image") || normalized.Contains("img") ||
            normalized.Contains("url") || normalized.Contains("src") || normalized.EndsWith("_wj") ||
            normalized.Contains("file") || normalized.EndsWith("_id")) return false;
        return normalized is "answer" or "text" or "content" or "stem" or "question" or "choices" or "options" ||
               normalized == "xt_xh" || normalized == "xt_nr" ||
               normalized.StartsWith("xt_") || normalized.StartsWith("xx") ||
               normalized.StartsWith("option") || normalized.StartsWith("choice");
    }

    private static object? ProjectValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Where(property => IsQuestionField(property.Name))
            .ToDictionary(property => property.Name, property => ProjectValue(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ProjectValue).Where(value => value is not null).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static async Task<string> AskAsync(string apiKey, string title, string questions,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            model = DeepSeekService.Model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildSystemPrompt(questions)
                },
                new { role = "user", content = BuildUserPrompt(title, questions) }
            },
            stream = false,
            temperature = 0.3
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = body;
            try
            {
                using JsonDocument error = JsonDocument.Parse(body);
                if (error.RootElement.TryGetProperty("error", out JsonElement errorObject) &&
                    errorObject.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                    detail = message.GetString() ?? body;
            }
            catch (JsonException) { }
            if (detail.Length > 500) detail = detail[..500];
            throw new HttpRequestException($"DeepSeek 返回 HTTP {(int)response.StatusCode}：{detail}");
        }
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidDataException("DeepSeek 没有返回解析内容。");
        string result = choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? string.Empty;
        if (result.Length == 0) throw new InvalidDataException("DeepSeek 返回内容为空。");
        return result;
    }

    private static string BuildSystemPrompt(string questions)
    {
        string prompt = "你是英语考试讲解老师。请按题号逐题解析：先还原题意，再指出考查点，逐项排除干扰项，说明正确答案依据，并补充必要的词汇或语法。听力题如果没有音频转写，不要臆造听力原文，说明仅基于题面和答案解析。输出适合打印的中文 Markdown，内容准确、简明。使用清晰的标题层级；答案速查优先使用表格；逐题解析使用小标题、加粗标签和列表，避免输出原始字段堆叠。";
        if (questions.Contains("\"口语题干\"", StringComparison.Ordinal))
            prompt += " 对于包含口语题干的题目，结合题干说明作答要求；没有提供口语题干的题目不要自行补写。";
        return prompt;
    }

    private static string BuildUserPrompt(string title, string questions)
    {
        string fields = questions.Contains("\"口语题干\"", StringComparison.Ordinal)
            ? "以下是试题字段、口语题干与标准答案："
            : "以下是试题字段与标准答案：";
        return $"试卷：{title}\n{fields}\n{questions}";
    }
}
