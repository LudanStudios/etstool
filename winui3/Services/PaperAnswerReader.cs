using System.Text.Json;
using System.Text.RegularExpressions;

namespace ETSAssistant.WinUI3.Services;

public sealed record PaperAnswerItem(int Number, string QuestionId, string Answer);

/// <summary>Reads the local answer key from an exam-pack folder.</summary>
public static class PaperAnswerReader
{
    public static IReadOnlyList<PaperAnswerItem> Read(string packDirectory)
    {
        var entries = new List<Entry>();
        int sequence = 0;
        foreach (string contentDirectory in Directory.EnumerateDirectories(packDirectory, "content_*")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string contentPath = Path.Combine(contentDirectory, "content.json");
            if (!File.Exists(contentPath)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(contentPath));
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    Text(root, "structure_type") != "collector.choose" ||
                    !root.TryGetProperty("info", out JsonElement info) ||
                    info.ValueKind != JsonValueKind.Object) continue;

                string contentId = Scalar(info, "stid");
                if (contentId.Length == 0) contentId = Path.GetFileName(contentDirectory)["content_".Length..];
                Dictionary<string, int> numbers = ReadDisplayNumbers(contentDirectory);
                if (!info.TryGetProperty("xtlist", out JsonElement questions) ||
                    questions.ValueKind != JsonValueKind.Array) continue;

                int index = 0;
                foreach (JsonElement question in questions.EnumerateArray())
                {
                    index++;
                    sequence++;
                    string questionNumber = Scalar(question, "xt_xh");
                    if (questionNumber.Length == 0) questionNumber = index.ToString();
                    string answer = Scalar(question, "answer").Trim().ToUpperInvariant();
                    if (answer is not ("A" or "B" or "C" or "D")) continue;
                    entries.Add(new Entry(
                        $"{contentId}_{questionNumber}",
                        numbers.TryGetValue(questionNumber, out int display) ? display : null,
                        sequence, answer));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }

        return entries
            .OrderBy(entry => entry.DisplayNumber ?? int.MaxValue)
            .ThenBy(entry => entry.Sequence)
            .Select((entry, index) => new PaperAnswerItem(entry.DisplayNumber ?? index + 1,
                entry.QuestionId, entry.CorrectAnswer))
            .ToList();
    }

    private static Dictionary<string, int> ReadDisplayNumbers(string contentDirectory)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        string path = Path.Combine(contentDirectory, "content2.json");
        if (!File.Exists(path)) return result;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            JsonElement list;
            if (root.ValueKind == JsonValueKind.Array) list = root;
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("info", out JsonElement info) &&
                     info.ValueKind == JsonValueKind.Object &&
                     info.TryGetProperty("xtlist", out JsonElement nested)) list = nested;
            else return result;
            if (list.ValueKind != JsonValueKind.Array) return result;

            int index = 0;
            foreach (JsonElement question in list.EnumerateArray())
            {
                index++;
                string questionNumber = Scalar(question, "xt_xh");
                if (questionNumber.Length == 0) questionNumber = index.ToString();
                Match match = Regex.Match(Scalar(question, "xt_nr"), @"[0-9]+");
                if (match.Success && int.TryParse(match.Value, out int number)) result[questionNumber] = number;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Scalar(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private sealed record Entry(string QuestionId, int? DisplayNumber, int Sequence, string CorrectAnswer);
}
