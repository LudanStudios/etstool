using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ETSAssistant.WinUI3.Services;

public sealed record UpdateCheckResult(bool UpdateAvailable, string Message, string Url, string? LatestRelease,
    string? LatestCommitSha, DateTimeOffset CheckedAt);

public sealed class GitHubUpdateService
{
    private const string Repository = "LudanStudios/etstool";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ETS-Tool", "1.0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? releaseTag = null, releaseUrl = null, commitSha = null, commitUrl = null;
        DateTimeOffset? releaseDate = null, commitDate = null;
        string? commitMessage = null;

        using (HttpResponseMessage response = await Http.GetAsync(
                   $"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                JsonElement root = doc.RootElement;
                releaseTag = String(root, "tag_name");
                releaseUrl = String(root, "html_url");
                if (DateTimeOffset.TryParse(String(root, "published_at"), out DateTimeOffset parsed)) releaseDate = parsed;
            }
            else if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.NoContent))
                throw new HttpRequestException($"GitHub Releases 返回 HTTP {(int)response.StatusCode}。");
        }

        using (HttpResponseMessage response = await Http.GetAsync(
                   $"https://api.github.com/repos/{Repository}/commits?per_page=1", cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub Commits 返回 HTTP {(int)response.StatusCode}。");
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                JsonElement item = doc.RootElement[0];
                commitSha = String(item, "sha");
                commitUrl = String(item, "html_url");
                if (item.TryGetProperty("commit", out JsonElement commit))
                {
                    if (commit.TryGetProperty("message", out JsonElement commitMessageValue) && commitMessageValue.ValueKind == JsonValueKind.String)
                        commitMessage = commitMessageValue.GetString()?.Split('\n')[0];
                    if (commit.TryGetProperty("committer", out JsonElement committer) &&
                        DateTimeOffset.TryParse(String(committer, "date"), out DateTimeOffset parsed))
                        commitDate = parsed;
                }
            }
        }

        Version current = typeof(GitHubUpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
        Version? latest = TryVersion(releaseTag);
        DateTimeOffset builtAt = ReadBuildTime();
        bool newerRelease = latest is not null && latest > current;
        bool newerCommit = commitDate is not null && commitDate > builtAt.AddMinutes(5);
        bool available = newerRelease || newerCommit;
        string url = newerRelease ? releaseUrl ?? "https://github.com/LudanStudios/etstool/releases" :
            commitUrl ?? "https://github.com/LudanStudios/etstool/commits/main";
        string message = !available
            ? "当前版本已是最新。"
            : newerRelease
                ? $"发现新版本 {releaseTag}（当前版本 {current.Major}.{current.Minor}.{current.Build}）。"
                : $"发现新提交 {commitSha?[..Math.Min(7, commitSha.Length)]}（{commitMessage ?? "有新的 GitHub 提交"}）。";
        return new UpdateCheckResult(available, message, url, releaseTag, commitSha, now);
    }

    private static DateTimeOffset ReadBuildTime()
    {
        try
        {
            string path = Path.Combine(AssistantService.Current.AppRoot, "VERSION.txt");
            string? line = File.ReadLines(path).FirstOrDefault(item => item.StartsWith("Build:", StringComparison.OrdinalIgnoreCase));
            if (line is not null && DateTimeOffset.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out DateTimeOffset built))
                return built.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return DateTimeOffset.MinValue;
    }

    private static Version? TryVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string clean = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(clean, out Version? version) ? version : null;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
