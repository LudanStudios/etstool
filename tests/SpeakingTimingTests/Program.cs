using System.Text;
using System.Text.Json;
using ETSAssistant.WinUI3.Services;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
Directory.CreateDirectory(root);
string Wav(double seconds)
{
    string path = Path.Combine(root, $"{seconds}.wav");
    using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
    int size = (int)(seconds * 2000);
    writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)1); writer.Write((short)1); writer.Write(1000); writer.Write(2000);
    writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(size); writer.Write(new byte[size]);
    return path;
}
void Setup(string type, double sourceSeconds, double replacementSeconds)
{
    DeepSeekService.Calls = 0; AliyunTtsService.Rates.Clear(); AliyunTtsService.Texts.Clear();
    string template = Path.Combine(root, "123", "template_1"); Directory.CreateDirectory(template);
    File.WriteAllText(Path.Combine(template, "ctrl.json"), "[{\"order\":\"1\",\"filetype\":\"wait\",\"playtime\":\"90\"},{\"order\":\"2\",\"filetype\":\"action.record\",\"playtime\":\"30\"}]");
    SpeakingAnswerService.Questions = [new(1, type, "Synthetic question", "Full assigned reading text.", "Full assigned reading text.")];
    SpeakingSessionStore.Stored = new("123", "flexible", DateTimeOffset.Now,
        [new(1, type, "Synthetic question", "Existing synthetic response", Wav(sourceSeconds))]) { PromptVersion = 2 };
    AliyunTtsService.Replacement = Wav(replacementSeconds);
}
try
{
    Check(SpeakingTimingService.MinimumSpeed(34, 30) == 1.19, "34s/30s must select 1.19x");
    Check(SpeakingTimingService.MinimumSpeed(5, 30) == 1, "Short audio must stay natural");
    Check(SpeakingTimingService.MinimumSpeed(44, 30) is null, "Oversize audio must request regeneration");
    var service = new SpeakingPrepService(root, root);

    Setup("speaking.kind_question_answer", 34, 10);
    var fitted = await service.PrepareAsync("123");
    Check(fitted.Errors.Count == 0 && fitted.Session!.Items[0].PlaybackSpeed == 1.19, "Fit existing audio before regenerating");
    Check(DeepSeekService.Calls == 0 && AliyunTtsService.Rates.Count == 0, "Already fitting audio must not call paid APIs");
    Check(fitted.Session!.Items[0].RecordingSeconds == 30, "Use recording time, not wait time");

    Setup("speaking.kind_question_answer", 60, 10);
    var shorter = await service.PrepareAsync("123");
    Check(shorter.Session!.Items[0].PlaybackSpeed == 1 && DeepSeekService.Calls == 1, "Regenerate oversize answer and measure result");
    Check(AliyunTtsService.Rates.SequenceEqual([0]), "Answer shortening should retain natural TTS rate");

    Setup("speaking.kind_read_aloud", 60, 35);
    var reading = await service.PrepareAsync("123");
    Check(reading.Errors.Count == 0 && DeepSeekService.Calls == 0, "Read-aloud must not be summarized");
    Check(AliyunTtsService.Texts.Single() == "Full assigned reading text.", "Preserve every assigned word");
    Check(AliyunTtsService.Rates.Single() == 100 && reading.Session!.Items[0].PlaybackSpeed <= 1.5, "Resynthesize compact read-aloud within playback cap");

    Setup("speaking.kind_question_answer", 100, 100);
    var failed = await service.PrepareAsync("123");
    Check(failed.Session is null && failed.Errors.Count > 0 && DeepSeekService.Calls == 3, "Bound retries and reject unfit output");
    Check(SpeakingPrepService.ActiveProgress is null, "Always release generation state");
    Console.WriteLine("PASS: minimal speed, authoritative recording limits, API-free reuse, shorter-answer regeneration, verbatim read-aloud resynthesis, bounded failure");

    string settingsPath = Path.Combine(root, "settings.json");
    SettingsFileStore.Write(settingsPath, JsonSerializer.Serialize(new { revision = 0, body = new string('0', 8192) }));
    using var stop = new CancellationTokenSource();
    using var started = new ManualResetEventSlim();
    int reads = 0;
    Task reader = Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                int revision = doc.RootElement.GetProperty("revision").GetInt32();
                Check(doc.RootElement.GetProperty("body").GetString() == new string((char)('0' + revision % 10), 8192), "Listener observed a partial settings edit");
                Interlocked.Increment(ref reads);
                started.Set();
            }
            // Like the listener, retain the last complete settings during replacement.
            catch (IOException) { Thread.Yield(); }
        }
    });
    Check(started.Wait(TimeSpan.FromSeconds(5)), "Concurrent reader failed to start");
    try
    {
        for (int i = 1; i <= 100; i++)
            SettingsFileStore.Write(settingsPath, JsonSerializer.Serialize(new { revision = i, body = new string((char)('0' + i % 10), 8192) }));
    }
    finally { stop.Cancel(); await reader; }
    Check(reads > 1, "Concurrent reader did not exercise settings updates");
    string saved = File.ReadAllText(settingsPath);
    bool blocked = false;
    using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { SettingsFileStore.Write(settingsPath, "incomplete replacement"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { blocked = true; }
    }
    Check(blocked && File.ReadAllText(settingsPath) == saved, "Failed save must preserve the prior complete file");
    Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Temporary settings files leaked");
    Console.WriteLine($"PASS: atomic settings writes during {reads} listener reads; failed-save retention and temp cleanup");
}
finally { Directory.Delete(root, true); }

namespace ETSAssistant.WinUI3.Services
{
    public record SpeakingQuestion(int Number, string Type, string Prompt, string StandardAnswer, string SourceText) { public string RequestText => Prompt; }
    public record SpeakingSessionItem(int Number, string Type, string Prompt, string Answer, string WavPath) { public double? RecordingSeconds {get;init;} public double PlaybackSpeed {get;init;}=1; }
    public record SpeakingSession(string PackId, string Mode, DateTimeOffset CreatedAt, IReadOnlyList<SpeakingSessionItem> Items) {public int PromptVersion {get;init;} }
    public class SpeakingAnswerService(string root) { public static IReadOnlyList<SpeakingQuestion> Questions=[]; public IReadOnlyList<SpeakingQuestion> Load(string id)=>Questions; }
    public class SpeakingSessionStore(string root) { public static SpeakingSession? Stored; public SpeakingSession? Load()=>Stored; public void Save(SpeakingSession session)=>Stored=session; public static bool IsUsableWavFile(string path)=>SpeakingTimingService.WavSeconds(path)>0; }
    public static class ApiKeyStore {public static bool HasKey=>true;}
    public static class AliyunTtsCredentialStore {public static bool HasCredentials=>true;}
    public class AppPreferencesService {public static AppPreferencesService Current {get;}=new();public string SpeakingMode=>"flexible";}
    public record DeepSeekResult(bool Success,string Text,string Error="");
    public class DeepSeekService(string root) {public static int Calls;public Task<DeepSeekResult> AskSpeakingAsync(string text,CancellationToken cancellationToken=default,string? timingInstruction=null){Calls++;return Task.FromResult(new DeepSeekResult(true,"A concise synthetic answer."));}}
    public record AliyunTtsResult(bool Success,string FilePath="",string Error="");
    public class AliyunTtsService(string root) {public static string Replacement="";public static List<int> Rates=[];public static List<string> Texts=[];public Task<AliyunTtsResult> SynthesizeToWavAsync(string text,CancellationToken cancellationToken=default,int speechRate=0){Rates.Add(speechRate);Texts.Add(text);return Task.FromResult(new AliyunTtsResult(true,Replacement));}}
    public class LocalizationService {public static LocalizationService Current {get;}=new();public string Translate(string key)=>key;public string Format(string key,params (string Name,object? Value)[] values)=>key+string.Join(" ",values.Select(x=>x.Value));}
    public static class DiagnosticLogService { public static void Warn(string name,string detail){} public static void Info(string name,string detail){} }
}
