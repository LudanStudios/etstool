using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ETSAssistant.WinUI3.Services;

public sealed record SpeakingQuestion(int Number, string Type, string Prompt, string StandardAnswer, string SourceText)
{
    public string Display => $"{Number}. [{Type}] {Prompt}";
}

/// <summary>
/// Reads the standard answers that the pack already contains for speaking tasks
/// (word/read/picture/dialogue), mirroring the fields used by the Markdown
/// converter. These answers are ready for TTS without calling an LLM.
/// </summary>
public sealed class SpeakingAnswerService
{
    private readonly string _dataRoot;

    public SpeakingAnswerService(string dataRoot) =>
        _dataRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);

    public string? FindRecentPackId()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-1);
            return Directory.EnumerateDirectories(_dataRoot)
                .Select(path => (Path: path, Name: Path.GetFileName(path)))
                .Where(item => Regex.IsMatch(item.Name, @"\A[0-9]+\z") &&
                               Directory.GetLastWriteTime(item.Path) >= cutoff)
                .OrderByDescending(item => Directory.GetLastWriteTime(item.Path))
                .Select(item => item.Name)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<SpeakingQuestion> Load(string packId)
    {
        var result = new List<SpeakingQuestion>();
        if (string.IsNullOrEmpty(packId) || !Regex.IsMatch(packId, @"\A[0-9]+\z")) return result;

        string packDirectory = Path.Combine(_dataRoot, packId);
        if (!Directory.Exists(packDirectory)) return result;

        int number = 0;
        foreach (string contentDirectory in Directory
                     .EnumerateDirectories(packDirectory, "content_*")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string path = Path.Combine(contentDirectory, "content.json");
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("info", out JsonElement info) ||
                    info.ValueKind != JsonValueKind.Object)
                    continue;

                switch (Text(root, "structure_type"))
                {
                    case "collector.word":
                    {
                        string text = Clean(Text(info, "value"));
                        if (text.Length == 0) break;
                        result.Add(new SpeakingQuestion(++number, "朗读", "Read the sentence aloud", text, text));
                        break;
                    }
                    case "collector.read":
                    {
                        string text = Clean(Text(info, "value"));
                        string answer = Clean(Text(info, "ai"));
                        if (answer.Length == 0) answer = text;
                        if (text.Length == 0 && answer.Length == 0) break;
                        result.Add(new SpeakingQuestion(++number, "朗读", "Read the passage aloud", answer, text));
                        break;
                    }
                    case "collector.picture":
                    {
                        string prompt = Clean(Text(info, "topic"));
                        if (prompt.Length == 0) prompt = "Describe the picture";
                        string answer = FirstStandard(info);
                        if (answer.Length == 0) break;
                        result.Add(new SpeakingQuestion(++number, "看图说话", prompt, answer, Clean(Text(info, "keypoint"))));
                        break;
                    }
                    case "collector.dialogue":
                    {
                        string source = Clean(Text(info, "value"));
                        if (!info.TryGetProperty("question", out JsonElement questions) ||
                            questions.ValueKind != JsonValueKind.Array)
                            break;
                        foreach (JsonElement question in questions.EnumerateArray())
                        {
                            string prompt = Clean(Text(question, "ask"));
                            string answer = FirstStandard(question);
                            if (prompt.Length == 0 && answer.Length == 0) continue;
                            result.Add(new SpeakingQuestion(++number, "口语问答", prompt, answer, source));
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { }
        }
        return result;
    }

    public SpeakingQuestion? Find(string packId, string prompt)
    {
        string needle = Normalize(prompt);
        if (needle.Length == 0) return null;
        IReadOnlyList<SpeakingQuestion> questions = Load(packId);
        return questions.FirstOrDefault(item => Normalize(item.Prompt) == needle)
               ?? questions.FirstOrDefault(item =>
                   Normalize(item.Prompt).Contains(needle, StringComparison.Ordinal) ||
                   needle.Contains(Normalize(item.Prompt), StringComparison.Ordinal));
    }

    private static string FirstStandard(JsonElement obj)
    {
        string explicitAnswer = Clean(Text(obj, "answer"));
        if (explicitAnswer.Length > 0) return explicitAnswer;

        if (obj.TryGetProperty("std", out JsonElement standards) &&
            standards.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in standards.EnumerateArray())
            {
                string value = Clean(Text(item, "value"));
                if (value.Length > 0) return value;
                string generated = Clean(Text(item, "ai"));
                if (generated.Length > 0) return generated;
            }
        }
        return string.Empty;
    }

    private static string Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string text = value;
        text = Regex.Replace(text, @"<\s*/?\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*p\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*p(?:\s+[^>]*)?>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*li\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r", string.Empty).Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"\bets_th\d+\b", string.Empty, RegexOptions.IgnoreCase);
        IEnumerable<string> lines = text.Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n", lines).Trim();
    }

    private static string Normalize(string? value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"\s+", " ").Trim();
}
