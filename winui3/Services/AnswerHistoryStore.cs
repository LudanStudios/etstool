using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ETSAssistant.WinUI3.Services;

public sealed record AnswerOption
{
    [JsonPropertyName("letter")] public string Letter { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

public sealed record AnswerRecord
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";
    [JsonPropertyName("pack_id")] public string PackId { get; init; } = "";
    [JsonPropertyName("question_id")] public string QuestionId { get; init; } = "";
    [JsonPropertyName("question_type")] public string QuestionType { get; init; } = "选择题";
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
    [JsonPropertyName("passage")] public string Passage { get; init; } = "";
    [JsonPropertyName("explanation")] public string Explanation { get; init; } = "";
    [JsonPropertyName("options")] public List<AnswerOption> Options { get; init; } = new();
    [JsonPropertyName("selected_answer")] public string SelectedAnswer { get; init; } = "";
    [JsonPropertyName("correct_answer")] public string CorrectAnswer { get; init; } = "";
    [JsonPropertyName("verified")] public bool Verified { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonIgnore] public bool Matches => SelectedAnswer == CorrectAnswer;
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Prompt)
        ? LocalizationService.Current.Format("history.question_title", ("questionId", QuestionId)) : Prompt;
    [JsonIgnore] public string TypeDisplay => AnswerHistoryStore.DisplayQuestionType(QuestionType);
    [JsonIgnore] public string LocationText => LocalizationService.Current.Format("history.location_text",
        ("paperId", PackId), ("type", TypeDisplay), ("time", Timestamp.ToLocalTime().ToString("MM-dd HH:mm")));
    [JsonIgnore] public string AnswerSummary => LocalizationService.Current.Format("history.answer_summary",
        ("selected", SelectedAnswer), ("correct", CorrectAnswer),
        ("match", LocalizationService.Current.Translate(Matches ? "history.matched" : "history.review")));
    [JsonIgnore] public string OptionsText => string.Join(Environment.NewLine, (Options ?? new()).Select(o => $"{o.Letter}. {o.Text}"));
}

public sealed record HistorySnapshot(IReadOnlyList<AnswerRecord> Records, int SkippedLines);
public sealed record AnalysisBucket(string Label, int Total, int Matched)
{
    public double Rate => Total == 0 ? 0 : Matched * 100.0 / Total;
    public string Description => Total == 0 ? LocalizationService.Current.Translate("history.no_records") :
        LocalizationService.Current.Format("history.description", ("total", Total), ("rate", Rate.ToString("0.#", CultureInfo.CurrentCulture)));
}

public sealed class AnswerHistoryStore
{
    public string HistoryPath { get; }
    private long _lastSize = -1;
    private DateTime _lastWrite;
    private HistorySnapshot _cached = new(Array.Empty<AnswerRecord>(), 0);

    public AnswerHistoryStore(string dataRoot) => HistoryPath = Path.Combine(Path.GetFullPath(dataRoot), "_assistant", "answer_history.jsonl");

    public HistorySnapshot Load()
    {
        var file = new FileInfo(HistoryPath);
        if (!file.Exists) { _lastSize = -1; return _cached = new(Array.Empty<AnswerRecord>(), 0); }
        if (file.Length == _lastSize && file.LastWriteTimeUtc == _lastWrite) return _cached;
        var latest = new Dictionary<(string Session, string Pack, string Question), AnswerRecord>();
        int skipped = 0;
        using var stream = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (reader.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > 1000000) { skipped++; continue; }
            try
            {
                var record = JsonSerializer.Deserialize<AnswerRecord>(line);
                if (record is null || record.SchemaVersion != 1 || record.Event != "answer_selected" ||
                    !record.Verified || record.Source != "assistant" || string.IsNullOrWhiteSpace(record.SessionId) ||
                    !Regex.IsMatch(record.PackId ?? "", @"\A[0-9]+\z") ||
                    !Regex.IsMatch(record.QuestionId ?? "", @"\A[0-9]+_[0-9]+\z") ||
                    record.Timestamp == default || record.SelectedAnswer is not ("A" or "B" or "C" or "D") ||
                    record.CorrectAnswer is not ("A" or "B" or "C" or "D"))
                { skipped++; continue; }
                latest[(record.SessionId, record.PackId, record.QuestionId)] = record;
            }
            catch (JsonException) { skipped++; }
        }
        _cached = new(latest.Values.OrderByDescending(r => r.Timestamp).ToList(), skipped);
        _lastSize = file.Length;
        _lastWrite = file.LastWriteTimeUtc;
        return _cached;
    }

    public static IReadOnlyList<AnalysisBucket> ByType(IEnumerable<AnswerRecord> records) => records
        .GroupBy(r => DisplayQuestionType(r.QuestionType))
        .Select(g => new AnalysisBucket(g.Key, g.Count(), g.Count(r => r.Matches)))
        .OrderByDescending(g => g.Total).ToList();

    public static IReadOnlyList<AnalysisBucket> LastSevenDays(IEnumerable<AnswerRecord> records, DateTime? today = null)
    {
        DateTime end = (today ?? DateTime.Today).Date;
        var groups = records.GroupBy(r => r.Timestamp.LocalDateTime.Date).ToDictionary(g => g.Key, g => g.ToList());
        return Enumerable.Range(0, 7).Select(i =>
        {
            DateTime date = end.AddDays(i - 6);
            List<AnswerRecord> day = groups.GetValueOrDefault(date) ?? new();
            return new AnalysisBucket(date.ToString("MM-dd"), day.Count, day.Count(r => r.Matches));
        }).ToList();
    }

    public static string ExportCsv(IEnumerable<AnswerRecord> records, IReadOnlyDictionary<string, string>? labels = null)
    {
        var csv = new StringBuilder();
        csv.AppendLine(L("history.csv_header"));
        foreach (var row in records.OrderBy(r => r.Timestamp))
        {
            string title = labels is not null && labels.TryGetValue(row.PackId, out string? label) ? label :
                F("dynamic.paper_title", ("paperId", row.PackId));
            string[] values = { row.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                row.SessionId, row.PackId, title, row.QuestionId, row.TypeDisplay, row.Title,
                row.SelectedAnswer, row.CorrectAnswer, L(row.Matches ? "history.yes" : "history.no"), row.Passage, row.OptionsText, row.Explanation };
            csv.AppendLine(string.Join(",", values.Select(CsvCell)));
        }
        return csv.ToString();
    }

    internal static string DisplayQuestionType(string? type) => type switch
    {
        "听力选择" => L("history.listening_choice"),
        "阅读选择" => L("history.reading_choice"),
        "选择题" or null or "" => L("history.choice"),
        _ => type
    };

    private static string L(string key) => LocalizationService.Current.Translate(key);
    private static string F(string key, params (string Name, object? Value)[] values) =>
        LocalizationService.Current.Format(key, values);

    private static string CsvCell(string? value)
    {
        value ??= "";
        string trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
