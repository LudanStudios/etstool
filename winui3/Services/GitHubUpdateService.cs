using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ETSAssistant.WinUI3.Services;

public sealed record UpdateCheckResult(bool UpdateAvailable, string Message, string Url, string? LatestRelease,
    string? LatestCommitSha, DateTimeOffset CheckedAt);

public sealed class GitHubUpdateService
{
    private const string Repository = "LudanStudios/etstool";
    private const string ProjectFile = "winui3/ETSAssistant.WinUI3.csproj";
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex VersionPattern = new(
        @"^v?(\d+)\.(\d+)\.(\d+)(?:\.\d+)?(?:[-+].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly ProductVersion _current;

    public GitHubUpdateService() : this(Http, typeof(GitHubUpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0))
    { }

    internal GitHubUpdateService(HttpClient http, Version currentVersion)
    {
        _http = http;
        _current = new ProductVersion(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ETS-Tool", "1.3.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? releaseTag = null, releaseUrl = null, commitSha = null, commitUrl = null;

        using (HttpResponseMessage response = await _http.GetAsync(
                   $"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                JsonElement root = doc.RootElement;
                releaseTag = String(root, "tag_name");
                releaseUrl = String(root, "html_url");
            }
            else if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.NoContent))
                throw new HttpRequestException(F("update.github_releases_http_error", ("status", (int)response.StatusCode)));
        }

        using (HttpResponseMessage response = await _http.GetAsync(
                   $"https://api.github.com/repos/{Repository}/commits?per_page=1", cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(F("update.github_commits_http_error", ("status", (int)response.StatusCode)));
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                JsonElement item = doc.RootElement[0];
                commitSha = String(item, "sha");
                commitUrl = String(item, "html_url");
            }
        }

        ProductVersion? latestRelease = ParseVersion(releaseTag);
        ProductVersion? latestCode = commitSha is null
            ? null : await ReadCodeVersionAsync(commitSha, cancellationToken);
        bool newerRelease = latestRelease is { } release && release.CompareTo(_current) > 0;
        bool newerCode = latestCode is { } code && code.CompareTo(_current) > 0;
        bool useRelease = newerRelease && (!newerCode || latestRelease!.Value.CompareTo(latestCode!.Value) >= 0);
        bool available = newerRelease || newerCode;
        string url = useRelease ? releaseUrl ?? $"https://github.com/{Repository}/releases" :
            commitUrl ?? $"https://github.com/{Repository}/commits/main";
        string message = !available
            ? L("update.current_version_latest")
            : useRelease
                ? F("update.new_release_found", ("latest", releaseTag), ("current", _current))
                : F("update.new_code_found", ("latest", latestCode), ("current", _current));
        return new UpdateCheckResult(available, message, url, releaseTag, commitSha, now);
    }

    private async Task<ProductVersion> ReadCodeVersionAsync(string commitSha, CancellationToken cancellationToken)
    {
        string url = $"https://raw.githubusercontent.com/{Repository}/{Uri.EscapeDataString(commitSha)}/{ProjectFile}";
        using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(F("update.github_version_http_error", ("status", (int)response.StatusCode)));
        string source = await response.Content.ReadAsStringAsync(cancellationToken);
        string? versionText = XDocument.Parse(source).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;
        return ParseVersion(versionText) ?? throw new InvalidDataException(L("update.github_version_invalid"));
    }

    private static ProductVersion? ParseVersion(string? value)
    {
        Match match = VersionPattern.Match(value?.Trim() ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out int major) &&
               int.TryParse(match.Groups[2].Value, out int minor) &&
               int.TryParse(match.Groups[3].Value, out int patch)
            ? new ProductVersion(major, minor, patch) : null;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string L(string key) => LocalizationService.Current.Translate(key);
    private static string F(string key, params (string Name, object? Value)[] values) =>
        LocalizationService.Current.Format(key, values);

    private readonly record struct ProductVersion(int Major, int Minor, int Patch) : IComparable<ProductVersion>
    {
        public int CompareTo(ProductVersion other)
        {
            int major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            int minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }
}
