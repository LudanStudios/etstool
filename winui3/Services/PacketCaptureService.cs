using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Optional CDP network capture for the 10086 DevTools endpoint.</summary>
public sealed class PacketCaptureService
{
    private static readonly Lazy<PacketCaptureService> Lazy = new(() => new PacketCaptureService(AssistantService.Current.DataRoot));
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };
    private static readonly Regex SubmittedPattern = new(
        "提交成功|已提交|答题结果|练习结果|成绩|本次练习完成|练习完成|完成答题|正确率|得分",
        RegexOptions.Compiled);
    public static PacketCaptureService Current => Lazy.Value;

    private readonly string _dataRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, CaptureRequest> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private int _nextId;
    private bool _armed = true;
    private string _pageUrl = "";

    private PacketCaptureService(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        CapturePath = Path.Combine(_dataRoot, "ets_packet_capture.log");
    }

    public string CapturePath { get; }
    public bool IsEnabled { get; private set; }
    public string Status { get; private set; } = "未启用";
    public event Action? StatusChanged;

    public void Enable()
    {
        if (IsEnabled) return;
        IsEnabled = true;
        _cts = new CancellationTokenSource();
        SetStatus("正在连接 10086…");
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Disable()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        _cts?.Cancel();
        _cts = null;
        try { _socket?.Abort(); } catch (Exception) { }
        SetStatus("未启用");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string? target = await FindTargetAsync(cancellationToken);
                if (target is null)
                {
                    SetStatus("等待 ETS 调试端口…");
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }

                using var socket = new ClientWebSocket();
                _socket = socket;
                await socket.ConnectAsync(new Uri(target), cancellationToken);
                Task receive = ReceiveLoopAsync(socket, cancellationToken);
                await SendCommandAsync("Network.enable", new
                {
                    maxTotalBufferSize = 100_000_000,
                    maxResourceBufferSize = 50_000_000,
                    maxPostDataSize = 1_000_000
                }, cancellationToken);
                SetStatus("抓包中，等待提交…");

                Task monitor = MonitorAsync(cancellationToken);
                await Task.WhenAny(receive, monitor);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception) { SetStatus("抓包连接中断，正在重试…"); }

            _socket = null;
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(2000, cancellationToken);
        }
        SetStatus("未启用");
    }

    private static async Task<string?> FindTargetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync("http://127.0.0.1:10086/json/list", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (JsonElement target in document.RootElement.EnumerateArray())
            {
                string type = Text(target, "type");
                string title = Text(target, "title");
                string socket = Text(target, "webSocketDebuggerUrl");
                if (type == "page" && title.Contains("E听说", StringComparison.Ordinal) && socket.StartsWith("ws://"))
                    return socket;
            }
        }
        catch (Exception) { }
        return null;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);
            bool submitted = await IsSubmittedAsync(cancellationToken);
            if (!submitted || !_armed) continue;

            _armed = false;
            SetStatus("检测到提交，3 秒后写入抓包…");
            await Task.Delay(3000, cancellationToken);
            int count = await DumpAsync(cancellationToken);
            SetStatus($"已抓取 {count} 个数据包");
            while (!cancellationToken.IsCancellationRequested && await IsSubmittedAsync(cancellationToken))
                await Task.Delay(2000, cancellationToken);
            _armed = true;
            SetStatus("抓包中，等待提交…");
        }
    }

    private async Task<bool> IsSubmittedAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonElement result = await SendCommandAsync("Runtime.evaluate", new
            {
                expression = "JSON.stringify({url:location.href,text:[document.body?.innerText||''," +
                             "...[...document.querySelectorAll('iframe')].map(f=>{try{return f.contentDocument?.body?.innerText||''}catch(_){return ''}})" +
                             "].join('\\n')})",
                returnByValue = true,
                awaitPromise = true
            }, cancellationToken);
            if (!result.TryGetProperty("result", out JsonElement value) ||
                !value.TryGetProperty("value", out JsonElement text) ||
                text.ValueKind != JsonValueKind.String) return false;
            using JsonDocument page = JsonDocument.Parse(text.GetString() ?? "{}");
            _pageUrl = Text(page.RootElement, "url");
            return SubmittedPattern.IsMatch(Text(page.RootElement, "text"));
        }
        catch (Exception) { return false; }
    }

    private async Task<int> DumpAsync(CancellationToken cancellationToken)
    {
        List<CaptureRequest> packets;
        lock (_gate)
        {
            DateTimeOffset cutoff = DateTimeOffset.Now.AddSeconds(-90);
            packets = _requests.Values
                .Where(request => request.Timestamp >= cutoff)
                .OrderBy(request => request.Timestamp)
                .ToList();
        }

        foreach (CaptureRequest packet in packets)
        {
            if (packet.Finished && packet.Body is null && packet.Error is null)
                await CaptureResponseBodyAsync(packet.RequestId);
        }

        var record = new
        {
            captured_at = DateTimeOffset.Now,
            page_url = _pageUrl,
            packets = packets.Select(request => new
            {
                timestamp = request.Timestamp,
                method = request.Method,
                type = request.Type,
                url = request.Url,
                status = request.Status,
                mime_type = request.MimeType,
                request_headers = request.Headers,
                request_body = request.PostData,
                response_headers = request.ResponseHeaders,
                response_body = request.Body,
                error = request.Error
            })
        };
        Directory.CreateDirectory(_dataRoot);
        await File.AppendAllTextAsync(CapturePath,
            JsonSerializer.Serialize(record) + Environment.NewLine, cancellationToken);
        lock (_gate) { _requests.Clear(); }
        return packets.Count;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            HandleMessage(text);
        }
    }

    private void HandleMessage(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt32(out int id) &&
                _pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
            {
                if (root.TryGetProperty("error", out JsonElement error))
                    completion.TrySetException(new InvalidOperationException(Text(error, "message")));
                else if (root.TryGetProperty("result", out JsonElement result))
                    completion.TrySetResult(result.Clone());
                else completion.TrySetResult(default);
                return;
            }

            string method = Text(root, "method");
            if (!root.TryGetProperty("params", out JsonElement parameters)) return;
            switch (method)
            {
                case "Network.requestWillBeSent": CaptureRequestWillBeSent(parameters); break;
                case "Network.responseReceived": CaptureResponse(parameters); break;
                case "Network.loadingFinished": CaptureFinished(parameters); break;
                case "Network.loadingFailed": CaptureFailed(parameters); break;
            }
        }
        catch (Exception) { }
    }

    private void CaptureRequestWillBeSent(JsonElement parameters)
    {
        string requestId = Text(parameters, "requestId");
        if (requestId.Length == 0 || !parameters.TryGetProperty("request", out JsonElement request)) return;
        string type = Text(parameters, "type");
        string method = Text(request, "method");
        if (type is not ("XHR" or "Fetch") && !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            return;

        var item = new CaptureRequest
        {
            RequestId = requestId,
            Timestamp = DateTimeOffset.Now,
            Url = Text(request, "url"),
            Method = method,
            Type = type,
            Headers = ReadHeaders(request, "headers")
        };
        if (request.TryGetProperty("postData", out JsonElement postData) && postData.ValueKind == JsonValueKind.String)
            item.PostData = Truncate(postData.GetString(), 100_000);
        lock (_gate) { _requests[requestId] = item; }
    }

    private void CaptureResponse(JsonElement parameters)
    {
        string requestId = Text(parameters, "requestId");
        if (!parameters.TryGetProperty("response", out JsonElement response)) return;
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out CaptureRequest? item)) return;
            item.Status = response.TryGetProperty("status", out JsonElement status) ? status.GetInt32() : null;
            item.MimeType = Text(response, "mimeType");
            item.ResponseHeaders = ReadHeaders(response, "headers");
        }
    }

    private void CaptureFinished(JsonElement parameters)
    {
        string requestId = Text(parameters, "requestId");
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out CaptureRequest? item)) return;
            item.Finished = true;
        }
    }

    private async Task CaptureResponseBodyAsync(string requestId)
    {
        try
        {
            string mime = "";
            lock (_gate)
            {
                if (_requests.TryGetValue(requestId, out CaptureRequest? known)) mime = known.MimeType;
            }
            if (mime.Length > 0 && !mime.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                !mime.Contains("text", StringComparison.OrdinalIgnoreCase) &&
                !mime.Contains("javascript", StringComparison.OrdinalIgnoreCase) &&
                !mime.Contains("xml", StringComparison.OrdinalIgnoreCase) &&
                !mime.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) return;

            JsonElement result = await SendCommandAsync("Network.getResponseBody", new { requestId }, CancellationToken.None);
            if (!result.TryGetProperty("body", out JsonElement body) || body.ValueKind != JsonValueKind.String) return;
            lock (_gate)
            {
                if (_requests.TryGetValue(requestId, out CaptureRequest? item))
                    item.Body = Truncate(body.GetString(), 200_000);
            }
        }
        catch (Exception) { }
    }

    private void CaptureFailed(JsonElement parameters)
    {
        string requestId = Text(parameters, "requestId");
        lock (_gate)
        {
            if (_requests.TryGetValue(requestId, out CaptureRequest? item))
                item.Error = Text(parameters, "errorText");
        }
    }

    private async Task<JsonElement> SendCommandAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = _socket ?? throw new InvalidOperationException("抓包连接尚未建立。");
        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            string payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await _sendLock.WaitAsync(cancellationToken);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
            finally { _sendLock.Release(); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using CancellationTokenRegistration registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            return await completion.Task;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    private static Dictionary<string, string> ReadHeaders(JsonElement parent, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!parent.TryGetProperty(name, out JsonElement headers) || headers.ValueKind != JsonValueKind.Object)
            return result;
        foreach (JsonProperty header in headers.EnumerateObject())
            result[header.Name] = header.Value.ValueKind == JsonValueKind.String
                ? header.Value.GetString() ?? "" : header.Value.ToString();
        return result;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string? Truncate(string? value, int limit) =>
        value is null ? null : value.Length <= limit ? value : value[..limit];

    private sealed class CaptureRequest
    {
        public string RequestId { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
        public string Url { get; init; } = "";
        public string Method { get; init; } = "";
        public string Type { get; init; } = "";
        public int? Status { get; set; }
        public string MimeType { get; set; } = "";
        public Dictionary<string, string> Headers { get; init; } = new();
        public Dictionary<string, string> ResponseHeaders { get; set; } = new();
        public string? PostData { get; set; }
        public string? Body { get; set; }
        public string? Error { get; set; }
        public bool Finished { get; set; }
    }
}
