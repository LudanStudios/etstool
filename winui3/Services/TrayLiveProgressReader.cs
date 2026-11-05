using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ETSAssistant.WinUI3.Services;

internal sealed record TrayLiveProgress(int? Answered, HashSet<int> DoneNumbers);

/// <summary>Reads displayed ETS progress without sending input or changing the page.</summary>
internal static class TrayLiveProgressReader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };
    private const string Expression = """
        (() => {
          const docs = [document, document.querySelector('iframe.examIframe')?.contentDocument].filter(Boolean);
          const text = docs.map(d => d.body?.innerText || '').join('\n');
          const count = text.match(/(?:\u5df2\u7b54|\u5df2\u5b8c\u6210|\u5df2\u4f5c\u7b54|answered|completed)\s*[:\uFF1A]?\s*(\d+)\s*(?:\/|\uFF0F|of)\s*(\d+)/i);
          const done = new Set();
          for (const doc of docs) for (const el of doc.querySelectorAll('button,a,li,div,span')) {
            const label = (el.textContent || '').trim();
            if (!/^\d{1,3}$/.test(label)) continue;
            const r = el.getBoundingClientRect();
            if (r.width < 22 || r.width > 140 || r.height < 18 || r.height > 90) continue;
            const nodes = [el, ...el.querySelectorAll('*')];
            if (nodes.some(n => {
              const cls = n.getAttribute('class') || '';
              const status = [n.getAttribute('aria-label'), n.getAttribute('data-status'), n.getAttribute('title')].join(' ');
              if (/(?:^|[\s_-])(?:answered|complete|completed|done|finished|success|correct|icon-check|icon-gou)(?:[\s_-]|$)/i.test(cls) && !/(?:un|not)[_-]/i.test(cls)) return true;
              if (/\u5df2\u7b54|\u5df2\u5b8c\u6210|\u5df2\u4f5c\u7b54|\banswered\b|\bcompleted\b/i.test(status)) return true;
              return ['::before', '::after'].some(p => /[\u2713\u2714\u221A]/.test(doc.defaultView.getComputedStyle(n,p).content || ''));
            })) done.add(Number(label));
          }
          return { answered: count ? Number(count[1]) : null, done: [...done] };
        })()
        """;

    public static async Task<TrayLiveProgress?> ReadAsync(string packId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            using var list = JsonDocument.Parse(await Http.GetStringAsync("http://127.0.0.1:10086/json/list", timeout.Token).ConfigureAwait(false));
            foreach (var target in list.RootElement.EnumerateArray())
            {
                if (!target.TryGetProperty("url", out var url) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(url.GetString() ?? "", @"[?&#]set_id=" + packId + @"(?:[&#]|$)") ||
                    !target.TryGetProperty("webSocketDebuggerUrl", out var address)) continue;
                if (!Uri.TryCreate(address.GetString(), UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback) continue;
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method = "Runtime.evaluate", @params = new { expression = Expression, returnByValue = true } });
                await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                byte[] buffer = new byte[8192];
                while (true)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close) return null;
                        message.Write(buffer, 0, received.Count);
                        if (message.Length > 1024 * 1024) return null;
                    } while (!received.EndOfMessage);
                    using var response = JsonDocument.Parse(message.ToArray());
                    var root = response.RootElement;
                    if (!root.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
                    if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var value)) return null;
                    int? answered = value.TryGetProperty("answered", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int n) ? n : null;
                    var done = value.GetProperty("done").EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
                    return new TrayLiveProgress(answered, done);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or WebSocketException or JsonException or InvalidOperationException) { }
        return null;
    }
}
