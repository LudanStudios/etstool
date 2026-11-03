using System.Net;
using System.Text;
using System.Text.Json;
using ETSAssistant.WinUI3.Services;

if (args.Contains("--live"))
{
    UpdateCheckResult result = await new GitHubUpdateService(new HttpClient(), new Version(1, 2, 2, 0)).CheckAsync();
    Console.WriteLine($"Live GitHub: {result.Message}");
    if (result.UpdateAvailable) throw new Exception("The current 1.2.3 release and code must not trigger an update.");
    return;
}

await Check("matching code and MSI", "1.2.3", "1.2.3", false, "当前版本已是最新。");
await Check("matching revision and metadata", "v1.2.3+msi", "1.2.3.42", false, "当前版本已是最新。");
await Check("newer MSI and code", "1.2.3", "1.2.3", true, "发现新版本 1.2.3");
await Check("newer code only", "1.2.3", "1.2.4", true, "发现新代码版本 1.2.4");
await Check("older remote sources", "1.0.0", "1.0.0", false, "当前版本已是最新。");
Console.WriteLine("All update check scenarios passed.");

static async Task Check(string name, string releaseVersion, string codeVersion, bool expectedUpdate, string expectedMessage)
{
    using var client = new HttpClient(new FakeGitHubHandler(releaseVersion, codeVersion));
    UpdateCheckResult result = await new GitHubUpdateService(client, new Version(1, 2, 2, 0)).CheckAsync();
    if (result.UpdateAvailable != expectedUpdate || !result.Message.Contains(expectedMessage, StringComparison.Ordinal))
        throw new Exception($"{name}: unexpected result: {result}");
    if (expectedUpdate)
    {
        string expectedPath = codeVersion == "1.2.3" && releaseVersion != "1.2.3" ? "/commit/" : "/releases/tag/";
        if (!result.Url.Contains(expectedPath, StringComparison.Ordinal))
            throw new Exception($"{name}: wrong update destination: {result.Url}");
    }
    Console.WriteLine($"Passed: {name}");
}

sealed class FakeGitHubHandler(string releaseVersion, string codeVersion) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        object body = path switch
        {
            "/repos/LudanStudios/etstool/releases/latest" => new
            {
                tag_name = releaseVersion,
                html_url = "https://github.com/LudanStudios/etstool/releases/tag/" + releaseVersion
            },
            "/repos/LudanStudios/etstool/commits" => new[]
            {
                new { sha = "abc123", html_url = "https://github.com/LudanStudios/etstool/commit/abc123" }
            },
            "/LudanStudios/etstool/abc123/winui3/ETSAssistant.WinUI3.csproj" =>
                $"<Project><PropertyGroup><Version>{codeVersion}</Version></PropertyGroup></Project>",
            _ => throw new Exception("Unexpected GitHub request: " + request.RequestUri)
        };
        string json = body is string source ? source : JsonSerializer.Serialize(body);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
