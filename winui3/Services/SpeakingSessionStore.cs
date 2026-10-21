using System.Text.Json;
using System.Text.Json.Serialization;

namespace ETSAssistant.WinUI3.Services;

public sealed record SpeakingSessionItem(int Number, string Type, string Prompt, string Answer, string WavPath)
{
    [JsonIgnore] public string Display => $"{Number}. [{Type}] {Prompt}";
    [JsonIgnore] public string WavName => Path.GetFileName(WavPath);
}

public sealed record SpeakingSession(string PackId, string Mode, DateTimeOffset CreatedAt,
    IReadOnlyList<SpeakingSessionItem> Items);

/// <summary>Persists the pre-generated TTS list for a whole speaking paper.</summary>
public sealed class SpeakingSessionStore
{
    private readonly string _path;

    public SpeakingSessionStore(string dataRoot) =>
        _path = Path.Combine(Path.GetFullPath(dataRoot), "_assistant", "speaking_session.json");

    public string FilePath => _path;

    public SpeakingSession? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<SpeakingSession>(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(SpeakingSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session), new System.Text.UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
