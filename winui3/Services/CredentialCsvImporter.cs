using System.Text;

namespace ETSAssistant.WinUI3.Services;

public sealed class CredentialCsvPreview
{
    public bool HasDeepSeek { get; init; }
    public bool HasAliyun { get; init; }
    public bool HasAny => HasDeepSeek || HasAliyun;
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    internal string? DeepSeekKey { get; init; }
    internal AliyunTtsCredentials? Aliyun { get; init; }
}

public sealed record CredentialImportResult(bool DeepSeekSaved, bool AliyunSaved, IReadOnlyList<string> Errors)
{
    public bool SavedAny => DeepSeekSaved || AliyunSaved;
}

/// <summary>
/// Imports DeepSeek and Aliyun TTS credentials from a small CSV file.
/// Everything is written through the existing DPAPI stores; no plaintext
/// credential file is produced and no export path is provided.
/// </summary>
public static class CredentialCsvImporter
{
    // Header form:
    // type,api_key,access_key_id,access_key_secret,app_key
    // deepseek,sk-xxxx,,,
    // aliyun,,LTAIxxxx,secret,appkey
    //
    // Positional form (no header):
    // deepseek,sk-xxxx
    // aliyun,LTAIxxxx,secret,appkey
    public static CredentialCsvPreview Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("找不到要导入的 CSV 文件。", path);

        string text = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
        List<List<string>> rows = ParseCsv(text);
        if (rows.Count == 0) throw new FormatException("CSV 为空。");

        Columns? header = BuildHeader(rows[0]);
        bool hasHeader = header is not null;
        int start = header is null ? 0 : 1;
        Columns columns = header ?? new Columns(0, 1, 2, 3, 4);

        var errors = new List<string>();
        string? deepSeekKey = null;
        AliyunTtsCredentials? aliyun = null;

        for (int index = start; index < rows.Count; index++)
        {
            List<string> row = rows[index];
            string type = Cell(row, columns.Type).Trim();
            if (type.Length == 0 || type.StartsWith('#')) continue;

            string normalized = Normalize(type);
            if (normalized is "deepseek" or "ds" or "deepseekapi" or "apikey" or "深度求索")
            {
                int keyIndex = hasHeader && columns.ApiKey >= 0 ? columns.ApiKey : 1;
                string key = Cell(row, keyIndex).Trim();
                if (key.Length < 8)
                {
                    errors.Add($"第 {index + 1} 行：DeepSeek API Key 为空或过短。");
                    continue;
                }
                deepSeekKey = key;
            }
            else if (normalized is "aliyun" or "ali" or "tts" or "aliyunts" or "阿里云" or "阿里云tts")
            {
                int idIndex = hasHeader && columns.AccessKeyId >= 0 ? columns.AccessKeyId : 1;
                int secretIndex = hasHeader && columns.AccessKeySecret >= 0 ? columns.AccessKeySecret : 2;
                int appIndex = hasHeader && columns.AppKey >= 0 ? columns.AppKey : 3;
                string id = Cell(row, idIndex).Trim();
                string secret = Cell(row, secretIndex).Trim();
                string app = Cell(row, appIndex).Trim();
                if (id.Length == 0 || secret.Length == 0 || app.Length == 0)
                {
                    errors.Add($"第 {index + 1} 行：阿里云凭据需要 AccessKey ID、AccessKey Secret 和 AppKey 三项。");
                    continue;
                }
                aliyun = new AliyunTtsCredentials(id, secret, app);
            }
            else
            {
                errors.Add($"第 {index + 1} 行：未知类型“{type}”，只支持 deepseek 或 aliyun。");
            }
        }

        if (deepSeekKey is null && aliyun is null && errors.Count == 0)
            errors.Add("没有找到可导入的凭据行。");

        return new CredentialCsvPreview
        {
            HasDeepSeek = deepSeekKey is not null,
            HasAliyun = aliyun is not null,
            DeepSeekKey = deepSeekKey,
            Aliyun = aliyun,
            Errors = errors
        };
    }

    public static CredentialImportResult Apply(CredentialCsvPreview preview)
    {
        var errors = new List<string>(preview.Errors);
        bool deepSeekSaved = false;
        bool aliyunSaved = false;

        if (preview.DeepSeekKey is not null)
        {
            try
            {
                ApiKeyStore.Save(preview.DeepSeekKey);
                deepSeekSaved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.Cryptography.CryptographicException)
            {
                errors.Add("DeepSeek 密钥保存失败：" + ex.Message);
            }
        }

        if (preview.Aliyun is not null)
        {
            try
            {
                AliyunTtsCredentialStore.Save(preview.Aliyun);
                aliyunSaved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
            {
                errors.Add("阿里云 TTS 凭据保存失败：" + ex.Message);
            }
        }

        return new CredentialImportResult(deepSeekSaved, aliyunSaved, errors);
    }

    private sealed record Columns(int Type, int ApiKey, int AccessKeyId, int AccessKeySecret, int AppKey);

    private static Columns? BuildHeader(IReadOnlyList<string> row)
    {
        int type = FindColumn(row, "type", "kind", "类型", "凭据类型");
        if (type < 0) return null;
        return new Columns(
            type,
            FindColumn(row, "apikey", "deepseekapikey", "deepseekkey", "key"),
            FindColumn(row, "accesskeyid", "ak", "accesskey"),
            FindColumn(row, "accesskeysecret", "sk", "secret"),
            FindColumn(row, "appkey", "appid"));
    }

    private static int FindColumn(IReadOnlyList<string> row, params string[] names)
    {
        for (int index = 0; index < row.Count; index++)
        {
            string normalized = Normalize(row[index]);
            if (names.Contains(normalized)) return index;
        }
        return -1;
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : string.Empty;

    private static string Normalize(string value) =>
        new string(value.Where(ch => !char.IsWhiteSpace(ch) && ch is not ('_' or '-'))
            .Select(char.ToLowerInvariant).ToArray());

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString().Trim());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString().Trim());
                    field.Clear();
                    if (row.Any(value => value.Length > 0)) rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        row.Add(field.ToString().Trim());
        if (row.Any(value => value.Length > 0)) rows.Add(row);
        return rows;
    }
}
