namespace ETSAssistant.WinUI3.Services;

public static class PaperCopyService
{
    public static string Format(string packId, IEnumerable<(int Number, string Answer)> choices,
        string dataRoot, string appRoot)
    {
        var sections = new List<string>();
        var choiceList = choices.ToList();
        if (choiceList.Count > 0)
            sections.Add(LocalizationService.Current.Translate("tray.choice") + Environment.NewLine +
                string.Join("  ", choiceList.Select(a => $"{a.Number}.{a.Answer}")));
        var questions = new SpeakingAnswerService(dataRoot).Load(packId);
        var session = new SpeakingSessionStore(appRoot).Load();
        var items = session?.PackId == packId ? session.Items : null;
        if (questions.Count > 0)
        {
            var lines = questions.Select(q =>
            {
                string? generated = items?.FirstOrDefault(item => item.Number == q.Number && item.Type == q.Type)?.Answer;
                string fallback = q.Type == "speaking.kind_read_aloud" && !string.IsNullOrWhiteSpace(q.SourceText)
                    ? q.SourceText : q.StandardAnswer;
                string answer = !string.IsNullOrWhiteSpace(generated) ? generated : fallback;
                return $"{q.Number}. {(string.IsNullOrWhiteSpace(answer) ? "—" : answer)}";
            });
            sections.Add(LocalizationService.Current.Translate("tray.speaking") + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }
}
