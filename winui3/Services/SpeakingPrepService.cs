using System.Collections.Generic;

namespace ETSAssistant.WinUI3.Services;

public sealed record SpeakingPrepProgress(int Completed, int Total, string Message);

public sealed record SpeakingPrepOutcome(SpeakingSession? Session, IReadOnlyList<string> Errors);
public sealed record SpeakingPrepProgressSnapshot(string PackId, int Completed, int Total, IReadOnlyList<int>? ReadyNumbers = null);

/// <summary>
/// Prepares a whole speaking paper for automation: resolves each answer,
/// synthesizes its TTS WAV, and saves the session used by the player.
/// </summary>
public sealed class SpeakingPrepService
{
    private const int CurrentPromptVersion = SpeakingTimingService.SessionVersion;
    private readonly string _dataRoot;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object ProgressLock = new();
    private static SpeakingPrepProgressSnapshot? _activeProgress;

    public static SpeakingPrepProgressSnapshot? ActiveProgress
    {
        get { lock (ProgressLock) return _activeProgress; }
    }

    private static void SetActiveProgress(SpeakingPrepProgressSnapshot? progress)
    {
        lock (ProgressLock) _activeProgress = progress;
    }
    private readonly SpeakingAnswerService _answers;
    private readonly DeepSeekService _deepSeek;
    private readonly AliyunTtsService _tts;
    private readonly SpeakingSessionStore _sessionStore;

    public SpeakingPrepService(string appRoot, string dataRoot)
    {
        _dataRoot = dataRoot;
        _answers = new SpeakingAnswerService(dataRoot);
        _deepSeek = new DeepSeekService(appRoot);
        _tts = new AliyunTtsService(appRoot);
        _sessionStore = new SpeakingSessionStore(appRoot);
    }

    public IReadOnlyList<SpeakingQuestion> LoadQuestions(string packId) => _answers.Load(packId);

    /// <summary>
    /// Returns the audio prepared for this paper on an earlier visit when it is
    /// still usable, so leaving and re-entering a simulated exam does not pay
    /// for the whole paper again.
    /// </summary>
    private SpeakingSession? FindReusableSession(string packId, string mode,
        IReadOnlyList<SpeakingQuestion> questions, IReadOnlyList<double> limits)
    {
        SpeakingSession? session = _sessionStore.Load();
        if (session is null || session.PackId != packId) return null;
        if (!string.Equals(session.Mode, mode, StringComparison.Ordinal)) return null;
        if (session.PromptVersion != CurrentPromptVersion) return null;
        if (session.Items.Count != questions.Count) return null;
        if (questions.Where(q => q.Type == "speaking.kind_read_aloud").Any(q =>
            session.Items.FirstOrDefault(item => item.Number == q.Number)?.Answer !=
            (string.IsNullOrWhiteSpace(q.SourceText) ? q.StandardAnswer : q.SourceText))) return null;
        if (!session.Items.Select(item => item.Number).SequenceEqual(questions.Select(question => question.Number)))
            return null;
        return session.Items.Select((item, i) => SpeakingSessionStore.IsUsableWavFile(item.WavPath) &&
            item.RecordingSeconds == (limits.Count == questions.Count ? (double?)limits[i] : null) &&
            SpeakingTimingService.MinimumSpeed(SpeakingTimingService.WavSeconds(item.WavPath), item.RecordingSeconds) is not null).All(valid => valid)
            ? session
            : null;
    }

    public async Task<SpeakingPrepOutcome> PrepareAsync(string packId,
        IProgress<SpeakingPrepProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SpeakingQuestion> questions = _answers.Load(packId);
        if (questions.Count == 0) return new SpeakingPrepOutcome(null, Array.Empty<string>());

        string mode = AppPreferencesService.Current.SpeakingMode;
        if (mode == "flexible" && !ApiKeyStore.HasKey)
            return new SpeakingPrepOutcome(null, new[]
            {
                LocalizationService.Current.Translate("common.invalid_credentials_error")
            });

        IReadOnlyList<double> limits = SpeakingTimingService.ReadLimits(_dataRoot, packId, questions.Count);
        SpeakingSession? reusable = FindReusableSession(packId, mode, questions, limits);
        if (reusable is not null)
        {
            progress?.Report(new SpeakingPrepProgress(reusable.Items.Count, questions.Count,
                LocalizationService.Current.Format("speaking.reusable_audio", ("count", reusable.Items.Count))));
            return new SpeakingPrepOutcome(reusable, Array.Empty<string>());
        }

        if (!AliyunTtsCredentialStore.HasCredentials)
        {
            return new SpeakingPrepOutcome(null, new[]
            {
                LocalizationService.Current.Translate("common.invalid_credentials_error")
            });
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetActiveProgress(new SpeakingPrepProgressSnapshot(packId, 0, questions.Count));
        try
        {
            var items = new List<SpeakingSessionItem>();
            var errors = new List<string>();
            int completed = 0;
            progress?.Report(new SpeakingPrepProgress(0, questions.Count,
                LocalizationService.Current.Format("speaking.preparing_questions",
                    ("count", questions.Count),
                    ("mode", LocalizationService.Current.Translate(mode == "flexible"
                        ? "speaking.mode_generated" : "speaking.mode_standard")))));

            SpeakingSession? previous = _sessionStore.Load();
            bool reusePrevious = previous?.PackId == packId && previous.Mode == mode &&
                previous.PromptVersion == CurrentPromptVersion && previous.Items.Count == questions.Count;
            foreach (SpeakingQuestion question in questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SpeakingPrepProgress(completed, questions.Count,
                    LocalizationService.Current.Format("speaking.processing_question",
                        ("current", question.Number), ("total", questions.Count),
                        ("kind", LocalizedQuestionType(question.Type)))));

                SpeakingSessionItem? prior = reusePrevious ? previous!.Items.FirstOrDefault(item =>
                    item.Number == question.Number && item.Type == question.Type && item.Prompt == question.Prompt &&
                    SpeakingSessionStore.IsUsableWavFile(item.WavPath)) : null;
                bool isReadAloud = question.Type == "speaking.kind_read_aloud";
                string readAloudText = string.IsNullOrWhiteSpace(question.SourceText) ? question.StandardAnswer : question.SourceText;
                if (isReadAloud && prior is not null && prior.Answer != readAloudText) prior = null;
                string answer = isReadAloud ? readAloudText : prior?.Answer ?? question.StandardAnswer;
                if (mode == "flexible" && prior is null && !isReadAloud)
                {
                    DeepSeekResult generated = await _deepSeek
                        .AskSpeakingAsync(question.RequestText, cancellationToken).ConfigureAwait(false);
                    if (generated.Success && generated.Text.Trim().Length > 0)
                        answer = generated.Text.Trim();
                    else
                        errors.Add(LocalizationService.Current.Format("speaking.deepseek_generation_failed",
                            ("question", question.Number), ("detail", LocalizationService.Current.Translate(generated.Error))));
                }
                if (string.IsNullOrWhiteSpace(answer))
                {
                    errors.Add(LocalizationService.Current.Format("speaking.no_answer_available",
                        ("question", question.Number)));
                    completed++;
                    SetActiveProgress(new SpeakingPrepProgressSnapshot(packId, completed, questions.Count, items.Select(item => item.Number).ToArray()));
                    continue;
                }

                AliyunTtsResult tts = prior is not null ? new AliyunTtsResult(true, prior.WavPath)
                    : await _tts.SynthesizeToWavAsync(answer, cancellationToken).ConfigureAwait(false);
                if (!tts.Success)
                {
                    errors.Add(LocalizationService.Current.Format("speaking.tts_failed",
                        ("question", question.Number), ("detail", LocalizationService.Current.Translate(tts.Error))));
                    completed++;
                    SetActiveProgress(new SpeakingPrepProgressSnapshot(packId, completed, questions.Count, items.Select(item => item.Number).ToArray()));
                    continue;
                }

                try
                {
                    double? limit = limits.Count == questions.Count ? limits[completed] : null;
                    items.Add(await FitAudioAsync(question, answer, tts, limit, cancellationToken).ConfigureAwait(false));
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(LocalizationService.Current.Format("speaking.timing_failed", ("question", question.Number), ("detail", ex.Message)));
                }
                completed++;
                SetActiveProgress(new SpeakingPrepProgressSnapshot(packId, completed, questions.Count, items.Select(item => item.Number).ToArray()));
                progress?.Report(new SpeakingPrepProgress(completed, questions.Count,
                    LocalizationService.Current.Format("speaking.generated_progress",
                        ("completed", completed), ("total", questions.Count),
                        ("kind", LocalizedQuestionType(question.Type)))));
            }

            if (items.Count == 0)
                return new SpeakingPrepOutcome(null, errors.Count > 0 ? errors : new[]
                {
                    LocalizationService.Current.Translate("speaking.no_audio_generated")
                });

            var session = new SpeakingSession(packId, mode, DateTimeOffset.Now, items)
            {
                PromptVersion = CurrentPromptVersion
            };
            _sessionStore.Save(session);
            progress?.Report(new SpeakingPrepProgress(items.Count, questions.Count,
                LocalizationService.Current.Format("speaking.completed_progress",
                    ("completed", items.Count), ("total", questions.Count))));
            return new SpeakingPrepOutcome(session, errors);
        }
        finally
        {
            SetActiveProgress(null);
            Gate.Release();
        }
    }

    private async Task<SpeakingSessionItem> FitAudioAsync(SpeakingQuestion question, string answer,
        AliyunTtsResult audio, double? limit, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            double duration = SpeakingTimingService.WavSeconds(audio.FilePath);
            double? speed = SpeakingTimingService.MinimumSpeed(duration, limit);
            if (speed is not null)
            {
                DiagnosticLogService.Info("speaking.timing.fitted", $"question={question.Number} source={duration:F2}s budget={limit:F2}s speed={speed:F2}x");
                return new SpeakingSessionItem(question.Number, question.Type, question.Prompt, answer, audio.FilePath)
                    { RecordingSeconds = limit, PlaybackSpeed = speed.Value };
            }
            if (attempt == 3 || limit is null || duration <= 0)
                throw new InvalidOperationException(LocalizationService.Current.Translate("speaking.timing_still_too_long"));
            cancellationToken.ThrowIfCancellationRequested();
            DiagnosticLogService.Info("speaking.timing.regenerating", $"question={question.Number} source={duration:F2}s budget={limit:F2}s attempt={attempt + 1}");
            int speechRate = 0;
            if (question.Type == "speaking.kind_read_aloud")
            {
                // Preserve assigned words; request a more compact synthesis, never summarize a read-aloud passage.
                speechRate = (attempt + 1) * 100;
                answer = question.SourceText.Length > 0 ? question.SourceText : question.StandardAnswer;
            }
            else
            {
                int words = System.Text.RegularExpressions.Regex.Matches(answer, @"\S+").Count;
                int maxWords = Math.Max(1, (int)Math.Floor(words * SpeakingTimingService.UsableSeconds(limit.Value) * 1.35 / duration * .9));
                var generated = await _deepSeek.AskSpeakingAsync(question.RequestText + "\n\nPrevious answer:\n" + answer,
                    cancellationToken, $"This is a timed response. Regenerate a shorter English answer, at most {maxWords} words. " +
                    "Keep the essential answer and required number of questions. Remove filler and examples. Return only spoken text.").ConfigureAwait(false);
                if (!generated.Success || string.IsNullOrWhiteSpace(generated.Text))
                    throw new InvalidOperationException(generated.Error);
                answer = generated.Text.Trim();
            }
            audio = await _tts.SynthesizeToWavAsync(answer, cancellationToken, speechRate).ConfigureAwait(false);
            if (!audio.Success) throw new InvalidOperationException(audio.Error);
        }
        throw new InvalidOperationException();
    }

    public async Task<SpeakingSession> RepairTimingAsync(string sessionPath, int index, double seconds,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = System.Text.Json.JsonSerializer.Deserialize<SpeakingSession>(await File.ReadAllTextAsync(sessionPath, cancellationToken))
                ?? throw new InvalidOperationException("Invalid speaking session");
            if (index < 0 || index >= session.Items.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var original = session.Items[index];
            var question = _answers.Load(session.PackId).FirstOrDefault(q => q.Number == original.Number)
                ?? throw new InvalidOperationException("Speaking question no longer exists");
            var item = await FitAudioAsync(question, original.Answer, new AliyunTtsResult(true, original.WavPath), seconds, cancellationToken).ConfigureAwait(false);
            var items = session.Items.ToArray();
            items[index] = item;
            session = session with { Items = items, PromptVersion = CurrentPromptVersion };
            _sessionStore.Save(session);
            return session;
        }
        finally { Gate.Release(); }
    }

    private static string LocalizedQuestionType(string type)
        => LocalizationService.Current.Translate(type);
}
