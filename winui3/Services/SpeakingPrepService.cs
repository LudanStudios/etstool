using System.Collections.Generic;

namespace ETSAssistant.WinUI3.Services;

public sealed record SpeakingPrepProgress(int Completed, int Total, string Message);

public sealed record SpeakingPrepOutcome(SpeakingSession? Session, IReadOnlyList<string> Errors);

/// <summary>
/// Prepares a whole speaking paper for automation: resolves each answer,
/// synthesizes its TTS WAV, and saves the session used by the player.
/// </summary>
public sealed class SpeakingPrepService
{
    private const int CurrentPromptVersion = 2;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly SpeakingAnswerService _answers;
    private readonly DeepSeekService _deepSeek;
    private readonly AliyunTtsService _tts;
    private readonly SpeakingSessionStore _sessionStore;

    public SpeakingPrepService(string appRoot, string dataRoot)
    {
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
        IReadOnlyList<SpeakingQuestion> questions)
    {
        SpeakingSession? session = _sessionStore.Load();
        if (session is null || session.PackId != packId) return null;
        if (!string.Equals(session.Mode, mode, StringComparison.Ordinal)) return null;
        if (mode == "flexible" && session.PromptVersion != CurrentPromptVersion) return null;
        if (session.Items.Count != questions.Count) return null;
        if (!session.Items.Select(item => item.Number).SequenceEqual(questions.Select(question => question.Number)))
            return null;
        return session.Items.All(item => SpeakingSessionStore.IsUsableWavFile(item.WavPath))
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

        SpeakingSession? reusable = FindReusableSession(packId, mode, questions);
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

            foreach (SpeakingQuestion question in questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SpeakingPrepProgress(completed, questions.Count,
                    LocalizationService.Current.Format("speaking.processing_question",
                        ("current", question.Number), ("total", questions.Count),
                        ("kind", LocalizedQuestionType(question.Type)))));

                string answer = question.StandardAnswer;
                if (mode == "flexible")
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
                    continue;
                }

                AliyunTtsResult tts = await _tts.SynthesizeToWavAsync(answer, cancellationToken)
                    .ConfigureAwait(false);
                if (!tts.Success)
                {
                    errors.Add(LocalizationService.Current.Format("speaking.tts_failed",
                        ("question", question.Number), ("detail", LocalizationService.Current.Translate(tts.Error))));
                    completed++;
                    continue;
                }

                items.Add(new SpeakingSessionItem(question.Number, question.Type, question.Prompt, answer,
                    tts.FilePath));
                completed++;
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
            Gate.Release();
        }
    }

    private static string LocalizedQuestionType(string type)
        => LocalizationService.Current.Translate(type);
}
