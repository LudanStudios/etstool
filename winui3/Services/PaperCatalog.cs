using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;

namespace ETSAssistant.WinUI3.Services;

public sealed record PaperEntry(string PackId, string Title, string FolderPath, int UnitCount,
    int ChoiceCount, long SizeBytes, DateTime Updated, DateTime Created, DateTime LastUsed, string TypeSummary,
    string? MarkdownPath, string? PdfPath, int UnreadableFiles)
{
    public bool IsConverted => MarkdownPath is not null || PdfPath is not null;
    public Visibility DetailedAnalysisVisibility => ApiKeyStore.HasKey ||
        AppPreferencesService.Current.ExperienceModeEnabled ? Visibility.Visible : Visibility.Collapsed;
    public string Summary => $"编号 {PackId} · {UnitCount} 个内容单元 · {ChoiceCount} 道选择题";
    public string ConversionText => PdfPath is not null ? "Markdown / PDF" : MarkdownPath is not null ? "Markdown" : "未转换";
    public string UpdatedText => Updated.ToString("yyyy-MM-dd HH:mm");
    public string CreatedText => Created.ToString("yyyy-MM-dd HH:mm");
    public string LastUsedText => LastUsed.ToString("yyyy-MM-dd HH:mm");
    public string SizeText => SizeBytes >= 1024L * 1024 ? $"{SizeBytes / (1024.0 * 1024):0.0} MB" : $"{SizeBytes / 1024.0:0.0} KB";
}

public sealed class PaperCatalog
{
    public string Root { get; }
    private string LabelsPath => Path.Combine(Root, "_assistant", "paper_labels.json");
    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
    };

    public PaperCatalog(string root) => Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

    public Dictionary<string, string> ReadLabels()
    {
        if (!File.Exists(LabelsPath)) return new(StringComparer.Ordinal);
        using var stream = new FileStream(LabelsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new(StringComparer.Ordinal);
    }

    public IReadOnlyList<PaperEntry> Scan()
    {
        if (!Directory.Exists(Root)) return Array.Empty<PaperEntry>();
        var labels = ReadLabels();
        var lastUsed = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            foreach (var group in new AnswerHistoryStore(Root).Load().Records.GroupBy(record => record.PackId))
                lastUsed[group.Key] = group.Max(record => record.Timestamp).LocalDateTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var result = new List<PaperEntry>();
        foreach (string folder in Directory.EnumerateDirectories(Root))
        {
            string id = Path.GetFileName(folder);
            if (!IsPackId(id) || IsLink(folder)) continue;
            int units = 0, choices = 0, unreadable = 0;
            var types = new Dictionary<string, int>();
            foreach (string content in Directory.EnumerateDirectories(folder, "content_*"))
            {
                string file = Path.Combine(content, "content.json");
                if (IsLink(content) || !File.Exists(file) || IsLink(file)) continue;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    var node = document.RootElement;
                    if (node.ValueKind != JsonValueKind.Object) { unreadable++; continue; }
                    string structure = Text(node, "structure_type");
                    units++;
                    string kind = structure switch
                    {
                        "collector.choose" => "选择题", "collector.word" => "词汇",
                        "collector.read" => "朗读", "collector.dialogue" => "情景对话",
                        "collector.picture" => "图片描述", _ => "其他内容"
                    };
                    int count = 1;
                    if (structure == "collector.choose" && node.TryGetProperty("info", out var info) &&
                        info.ValueKind == JsonValueKind.Object && info.TryGetProperty("xtlist", out var questions) &&
                        questions.ValueKind == JsonValueKind.Array)
                    {
                        count = questions.GetArrayLength();
                        choices += count;
                    }
                    types[kind] = types.GetValueOrDefault(kind) + count;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { unreadable++; }
            }
            long bytes = 0;
            DateTime updated = Directory.GetLastWriteTime(folder);
            foreach (string file in Directory.EnumerateFiles(folder, "*", RecursiveFiles))
            {
                try
                {
                    var info = new FileInfo(file);
                    bytes += info.Length;
                    if (info.LastWriteTime > updated) updated = info.LastWriteTime;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            string? md = FindOutput(folder, ".md"), pdf = FindOutput(folder, ".pdf");
            string summary = types.Count == 0 ? "尚无可读取的题目内容" : string.Join(" · ", types.Select(p => $"{p.Key} {p.Value}"));
            DateTime created = Directory.GetCreationTime(folder);
            DateTime used = lastUsed.TryGetValue(id, out DateTime recorded) && recorded > updated ? recorded : updated;
            result.Add(new(id, labels.GetValueOrDefault(id, $"试卷 {id}"), folder, units, choices,
                bytes, updated, created, used, summary, md, pdf, unreadable));
        }
        return result.OrderByDescending(p => p.Updated).ThenBy(p => p.PackId, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<PaperAnswerItem> ReadAnswers(string packId) =>
        PaperAnswerReader.Read(ValidatePack(packId));

    public void Rename(string packId, string title)
    {
        ValidatePack(packId);
        title = title.Trim();
        if (title.Length is < 1 or > 80) throw new ArgumentException("名称需要包含 1 到 80 个字符。");
        var labels = ReadLabels();
        labels[packId] = title;
        Directory.CreateDirectory(Path.GetDirectoryName(LabelsPath)!);
        string temporary = LabelsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(labels), new UTF8Encoding(false));
            File.Move(temporary, LabelsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string Import(string source)
    {
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        string id = Path.GetFileName(source);
        if (!Directory.Exists(source) || !IsPackId(id)) throw new ArgumentException("请选择以数字编号命名的完整试卷文件夹。");
        if (Root.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, Root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择一份试卷文件夹，而不是整个数据目录。");
        if (!Directory.EnumerateDirectories(source, "template_*").Any() ||
            !Directory.EnumerateDirectories(source, "content_*").Any())
            throw new InvalidDataException("试卷需要包含 template_ 和 content_ 子文件夹。");
        string target = Path.Combine(Root, id);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"编号 {id} 的试卷已存在，请勿重复导入。");
        string staging = Path.Combine(Root, "_assistant", "imports", Guid.NewGuid().ToString("N"));
        try
        {
            CopyFolder(source, staging);
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        return id;
    }

    public void Delete(string packId)
    {
        string path = ValidatePack(packId);
        EnsureNoLinks(path);
        Directory.Delete(path, true);
    }

    public async Task ConvertAsync(string packId, string enginePath)
    {
        string path = ValidatePack(packId);
        if (!File.Exists(enginePath)) throw new FileNotFoundException("找不到转换组件，请重新生成完整程序。", enginePath);
        var start = new ProcessStartInfo(enginePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(enginePath)!,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--convert-pack");
        start.ArgumentList.Add(path);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动试卷转换。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? output : error;
            if (detail.Length > 1200) detail = detail[^1200..];
            throw new InvalidOperationException("转换未能全部完成。" + Environment.NewLine + detail.Trim());
        }
    }

    private string ValidatePack(string packId)
    {
        if (!IsPackId(packId)) throw new ArgumentException("无效的试卷编号。");
        string path = Path.GetFullPath(Path.Combine(Root, packId));
        if (!string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(path) || IsLink(path)) throw new InvalidOperationException("试卷目录不存在或不是受管理的本机目录。");
        return path;
    }

    private static bool IsPackId(string value) => Regex.IsMatch(value, @"\A[0-9]+\z");
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void EnsureNoLinks(string folder)
    {
        if (IsLink(folder)) throw new InvalidOperationException("试卷含有链接目录，无法安全执行此操作。");
        foreach (string item in Directory.EnumerateFileSystemEntries(folder))
        {
            if (IsLink(item)) throw new InvalidOperationException("试卷含有链接文件，无法安全执行此操作。");
            if (Directory.Exists(item)) EnsureNoLinks(item);
        }
    }

    private static void CopyFolder(string source, string target)
    {
        if (IsLink(source)) throw new InvalidOperationException("暂不支持导入包含链接目录的试卷。");
        Directory.CreateDirectory(target);
        foreach (string item in Directory.EnumerateFileSystemEntries(source))
        {
            if (IsLink(item)) throw new InvalidOperationException("暂不支持导入包含链接文件的试卷。");
            string destination = Path.Combine(target, Path.GetFileName(item));
            if (Directory.Exists(item)) CopyFolder(item, destination);
            else File.Copy(item, destination, false);
        }
    }

    private static string? FindOutput(string folder, string extension)
    {
        string converted = Path.Combine(folder, "converted");
        if (!Directory.Exists(converted) || IsLink(converted)) return null;
        return Directory.EnumerateFiles(converted, "*" + extension).Where(p => !IsLink(p)).OrderBy(p => p).FirstOrDefault();
    }

    private static string Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
