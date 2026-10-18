using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ETSAssistant.WinUI3.Services;

public sealed record CurrentPaperAnswer(int Number, string QuestionId, string CorrectAnswer, string SelectedAnswer)
{
    public bool Answered => SelectedAnswer.Length > 0;
    public bool Matches => Answered && SelectedAnswer == CorrectAnswer;
}

public sealed record CurrentPaperSnapshot(string PackId, string Title, IReadOnlyList<CurrentPaperAnswer> Answers,
    DateTime? LastActivity)
{
    public int Total => Answers.Count;
    public int Answered => Answers.Count(answer => answer.Answered);
    public int Correct => Answers.Count(answer => answer.Matches);
    public bool Complete => Total > 0 && Answered >= Total;
}

/// <summary>Resolves the paper ETS currently has open and joins its local answer key with the answer history.</summary>
public sealed class CurrentPaperService
{
    private const string DevToolsEndpoint = "http://127.0.0.1:10086/json/list";
    private const string AppTitle = "E听说";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(900) };
    private readonly string _dataRoot;
    private readonly AnswerHistoryStore _history;
    private readonly PaperCatalog _catalog;

    public CurrentPaperService(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
        _history = new AnswerHistoryStore(_dataRoot);
        _catalog = new PaperCatalog(_dataRoot);
    }

    public async Task<CurrentPaperSnapshot?> LoadAsync()
    {
        string? activePackId = await FindActivePackIdAsync().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            IReadOnlyList<AnswerRecord> history = _history.Load().Records;
            string? packId = activePackId ?? FindRecentHistoryPack(history) ?? FindRecentPackFolder();
            if (packId is null || !Regex.IsMatch(packId, @"\A[0-9]+\z")) return null;

            string packDirectory = Path.Combine(_dataRoot, packId);
            if (!Directory.Exists(packDirectory)) return null;
            try { return BuildSnapshot(packId, packDirectory, history); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }).ConfigureAwait(false);
    }

    private static async Task<string?> FindActivePackIdAsync()
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(DevToolsEndpoint).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (JsonElement target in document.RootElement.EnumerateArray())
            {
                if (Text(target, "type") != "page" ||
                    !Text(target, "title").Contains(AppTitle, StringComparison.Ordinal)) continue;
                Match match = Regex.Match(Text(target, "url"), @"[?&#]set_id=([0-9]+)");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { }
        return null;
    }

    private string? FindRecentHistoryPack(IReadOnlyList<AnswerRecord> history)
    {
        AnswerRecord? latest = history.FirstOrDefault();
        if (latest is null || DateTimeOffset.Now - latest.Timestamp > TimeSpan.FromMinutes(30)) return null;
        return latest.PackId;
    }

    private string? FindRecentPackFolder()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddMinutes(-15);
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

    private CurrentPaperSnapshot BuildSnapshot(string packId, string packDirectory,
        IReadOnlyList<AnswerRecord> history)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { }
        }

        IGrouping<string, AnswerRecord>? session = history
            .Where(record => record.PackId == packId)
            .GroupBy(record => record.SessionId)
            .OrderByDescending(group => group.Max(record => record.Timestamp))
            .FirstOrDefault();
        var selected = session?
            .GroupBy(record => record.QuestionId)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(record => record.Timestamp).First().SelectedAnswer)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var answers = new List<CurrentPaperAnswer>();
        int position = 0;
        foreach (Entry entry in entries.OrderBy(item => item.DisplayNumber ?? int.MaxValue)
                     .ThenBy(item => item.Sequence))
        {
            position++;
            selected.TryGetValue(entry.QuestionId, out string? chosen);
            answers.Add(new CurrentPaperAnswer(entry.DisplayNumber ?? position, entry.QuestionId,
                entry.CorrectAnswer, chosen ?? string.Empty));
        }

        string title;
        try
        {
            title = _catalog.ReadLabels().TryGetValue(packId, out string? label) ? label : $"试卷 {packId}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            title = $"试卷 {packId}";
        }

        DateTime? lastActivity = session?.Max(record => record.Timestamp).LocalDateTime;
        return new CurrentPaperSnapshot(packId, title, answers, lastActivity);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { }
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
