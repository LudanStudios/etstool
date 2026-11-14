using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Uses the paper's action.record durations, never its preparation/listening timers.</summary>
public static class SpeakingTimingService
{
    public const int SessionVersion = 4;
    public const double MaximumSpeed = 1.5;

    public static double UsableSeconds(double seconds) => Math.Max(0, seconds - Math.Min(1.25, seconds * .15));

    public static double? MinimumSpeed(double duration, double? seconds)
    {
        if (!double.IsFinite(duration) || duration <= 0) return null;
        if (seconds is null) return 1;
        double usable = UsableSeconds(seconds.Value);
        if (usable <= 0) return MaximumSpeed;
        double speed = Math.Max(1, Math.Ceiling((duration / usable - 1e-9) * 100) / 100);
        return Math.Min(speed, MaximumSpeed);
    }

    public static IReadOnlyList<double> ReadLimits(string dataRoot, string packId, int count)
    {
        var candidates = new List<double[]>();
        string directory = Path.Combine(dataRoot, packId);
        if (!Directory.Exists(directory)) return Array.Empty<double>();
        foreach (string template in Directory.EnumerateDirectories(directory, "template_*"))
        {
            try
            {
                string path = Path.Combine(template, "ctrl.json");
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                var limits = doc.RootElement.EnumerateArray()
                    .Where(x => x.TryGetProperty("filetype", out var type) && type.GetString() == "action.record")
                    .OrderBy(x => x.TryGetProperty("order", out var order) && double.TryParse(order.ToString(), out double n) ? n : double.MaxValue)
                    .Select(x => x.TryGetProperty("playtime", out var time) && double.TryParse(time.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0)
                    .ToArray();
                if (limits.Length == count && limits.All(x => double.IsFinite(x) && x > 0 && x <= 600)) candidates.Add(limits);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { DiagnosticLogService.Warn("speaking.timing.template_read_failed", ex.Message); }
        }
        if (candidates.Count > 0 && candidates.All(x => x.SequenceEqual(candidates[0]))) return candidates[0];
        DiagnosticLogService.Warn("speaking.timing.unavailable", $"pack={packId} questions={count}; ambiguous or missing recording durations");
        return Array.Empty<double>();
    }

    public static double WavSeconds(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII);
            if (new string(reader.ReadChars(4)) != "RIFF") return 0;
            reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") return 0;
            uint bytesPerSecond = 0;
            long dataBytes = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                string id = new(reader.ReadChars(4));
                uint size = reader.ReadUInt32();
                long start = stream.Position;
                if (size > stream.Length - start) return 0;
                if (id == "fmt " && size >= 16)
                {
                    stream.Position += 8;
                    bytesPerSecond = reader.ReadUInt32();
                }
                if (id == "data") dataBytes += size;
                stream.Position = start + size + (size & 1);
            }
            return bytesPerSecond > 0 ? dataBytes / (double)bytesPerSecond : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
