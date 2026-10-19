using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using ETSAssistant.WinUI3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class ToolsPage : Page
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };
    private readonly AssistantService _service = AssistantService.Current;
    private readonly PacketCaptureService _capture = PacketCaptureService.Current;
    private readonly DeepSeekService _deepSeek = new(AssistantService.Current.AppRoot);
    private readonly SpeakingAnswerService _speaking = new(AssistantService.Current.DataRoot);
    private bool _updatingSpeakingMode;
    private string? _speakingPackId;
    private IReadOnlyList<SpeakingPaper> _speakingPapers = Array.Empty<SpeakingPaper>();
    private bool _portAvailable;
    private bool _updatingCapture;

    public ToolsPage()
    {
        InitializeComponent();
        _capture.StatusChanged += OnCaptureStatusChanged;
        Unloaded += (_, _) => _capture.StatusChanged -= OnCaptureStatusChanged;
        Loaded += async (_, _) =>
        {
            RefreshLogSummary();
            RefreshDeepSeekStatus();
            UpdateSpeakingModeUi();
            UpdateCaptureUi();
            EnvironmentText.Text = BuildEnvironmentText();
            await ProbePortAsync();
        };
    }

    private void OnCaptureStatusChanged() => DispatcherQueue.TryEnqueue(UpdateCaptureUi);

    private void UpdateCaptureUi()
    {
        if (!_updatingCapture)
        {
            _updatingCapture = true;
            CaptureToggle.IsOn = _capture.IsEnabled;
            CaptureStatusText.Text = _capture.Status;
            CapturePathText.Text = "抓包文件：" + _capture.CapturePath;
            _updatingCapture = false;
        }
    }

    private void CaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingCapture) return;
        if (CaptureToggle.IsOn) _capture.Enable();
        else _capture.Disable();
        UpdateCaptureUi();
    }

    private void OpenCapture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_capture.CapturePath))
            {
                ShowMessage("暂无抓包文件", "启用抓包模式并完成一次答题提交后，这里会生成抓包记录。",
                    InfoBarSeverity.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(_capture.CapturePath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage("无法打开抓包文件", ex.Message, InfoBarSeverity.Error); }
    }

    private async Task<bool> ProbePortAsync()
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync("http://127.0.0.1:10086/json/version");
            _portAvailable = response.IsSuccessStatusCode;
        }
        catch (Exception) { _portAvailable = false; }

        PortDot.Fill = new SolidColorBrush(_portAvailable
            ? Color.FromArgb(255, 34, 197, 94)
            : Color.FromArgb(255, 148, 163, 184));
        PortStatusText.Text = _portAvailable ? "端口可用" : "未连接";
        EnvironmentText.Text = BuildEnvironmentText();
        return _portAvailable;
    }

    private async void OpenList_Click(object sender, RoutedEventArgs e) => await ShowEndpointAsync("json/list");
    private async void OpenVersion_Click(object sender, RoutedEventArgs e) => await ShowEndpointAsync("json/version");

    private async Task ShowEndpointAsync(string path)
    {
        string url = $"http://127.0.0.1:10086/{path}";
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                ShowMessage("端口返回错误", $"{url} 返回 HTTP {(int)response.StatusCode}。", InfoBarSeverity.Warning);
                return;
            }

            var viewer = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = body, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                }
            };
            var dialog = new ContentDialog
            {
                Title = $"127.0.0.1:10086/{path}",
                Content = viewer,
                PrimaryButtonText = "复制 JSON",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) CopyText(body);
        }
        catch (Exception)
        {
            ShowMessage("端口未开放", "请先打开带 --remote-debugging-port=10086 的 ETS，再查看调试端口。",
                InfoBarSeverity.Warning);
        }
    }

    private void RefreshDeepSeekStatus()
    {
        string keyState = ApiKeyStore.HasKey ? "API Key：已配置" : "API Key：未配置（请到“答题设置”填写）";
        string attachment = _deepSeek.FindAttachment() ?? "未找到 llm_stt.md";
        string mode = AppPreferencesService.Current.SpeakingMode == "flexible" ? "灵活模式" : "标答模式";
        DeepSeekStatusText.Text = $"{mode} · {keyState} · 模型 {DeepSeekService.Model} · 附件：{attachment}";
    }

    private void UpdateSpeakingModeUi()
    {
        _updatingSpeakingMode = true;
        SpeakingModeBox.SelectedIndex = AppPreferencesService.Current.SpeakingMode == "flexible" ? 1 : 0;
        _updatingSpeakingMode = false;
        AskDeepSeekText.Text = AppPreferencesService.Current.SpeakingMode == "flexible"
            ? "发送到 DeepSeek" : "提取标答";
    }

    private void SpeakingModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSpeakingMode) return;
        if (SpeakingModeBox.SelectedItem is ComboBoxItem { Tag: string mode })
        {
            AppPreferencesService.Current.SetSpeakingMode(mode);
            AskDeepSeekText.Text = mode == "flexible" ? "发送到 DeepSeek" : "提取标答";
            RefreshDeepSeekStatus();
        }
    }

    private async void LoadSpeakingQuestions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<SpeakingPaper> papers = await Task.Run(() => _speaking.LoadAllPapers());
            _speakingPapers = papers;
            SpeakingPaperBox.ItemsSource = papers;
            if (papers.Count == 0)
            {
                ShowMessage("没有口语题", "本机试卷里没有找到朗读、看图或问答类口语题。",
                    InfoBarSeverity.Warning);
                return;
            }
            SpeakingPaperBox.SelectedIndex = 0;
            int total = papers.Sum(paper => paper.Questions.Count);
            ShowMessage("已读取口语题", $"共 {papers.Count} 份试卷、{total} 道口语题。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("读取口语题失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SpeakingPaperBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeakingPaperBox.SelectedItem is not SpeakingPaper paper) return;
        _speakingPackId = paper.PackId;
        SpeakingQuestionBox.ItemsSource = paper.Questions;
        if (paper.Questions.Count > 0) SpeakingQuestionBox.SelectedIndex = 0;
    }

    private void SpeakingQuestionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeakingQuestionBox.SelectedItem is not SpeakingQuestion question) return;
        SpeakingStemBox.Text = question.RequestText;
        if (AppPreferencesService.Current.SpeakingMode != "standard") return;
        if (question.StandardAnswer.Length == 0)
        {
            ShowMessage("这道题没有标答", "试卷里没有为这道口语题提供推荐答案，请改用灵活模式。",
                InfoBarSeverity.Warning);
            return;
        }
        SpeakingResultBox.Text = question.StandardAnswer;
    }

    private async Task<SpeakingQuestion?> ResolveSpeakingQuestionAsync(string stem)
    {
        if (SpeakingQuestionBox.SelectedItem is SpeakingQuestion selected &&
            string.Equals(selected.RequestText.Trim(), stem.Trim(), StringComparison.Ordinal))
            return selected;

        if (_speakingPackId is not null)
        {
            SpeakingQuestion? current = _speaking.Find(_speakingPackId, stem);
            if (current is not null) return current;
        }

        foreach (SpeakingPaper paper in _speakingPapers)
        {
            SpeakingQuestion? match = _speaking.Find(paper.PackId, stem);
            if (match is not null) return match;
        }

        _speakingPackId ??= await CurrentPaperService.FindActivePackIdAsync() ?? _speaking.FindRecentPackId();
        return _speakingPackId is null ? null : _speaking.Find(_speakingPackId, stem);
    }

    private async void AskDeepSeek_Click(object sender, RoutedEventArgs e)
    {
        string stem = SpeakingStemBox.Text.Trim();
        if (stem.Length == 0)
        {
            ShowMessage("缺少题干", "请先输入口语题干，再发送到 DeepSeek。", InfoBarSeverity.Warning);
            return;
        }

        if (AppPreferencesService.Current.SpeakingMode != "flexible")
        {
            SpeakingQuestion? question = await ResolveSpeakingQuestionAsync(stem);
            if (question is null || question.StandardAnswer.Length == 0)
            {
                ShowMessage("没有找到标答",
                    "当前试卷里没有匹配的标准答案。可以先点“读取当前试卷口语题”，或切换到灵活模式。",
                    InfoBarSeverity.Warning);
                return;
            }
            SpeakingResultBox.Text = question.StandardAnswer;
            ShowMessage("已提取标答",
                $"标答来自试卷 {_speakingPackId ?? "当前试卷"}，共 {question.StandardAnswer.Length} 个字符。",
                InfoBarSeverity.Success);
            return;
        }

        AskDeepSeekButton.IsEnabled = false;
        DeepSeekProgress.IsActive = true;
        try
        {
            DeepSeekResult result = await _deepSeek.AskSpeakingAsync(stem);
            if (result.Success)
            {
                SpeakingResultBox.Text = result.Text;
                ShowMessage("DeepSeek 已返回", $"模型 {DeepSeekService.Model} 返回 {result.Text.Length} 个字符。",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage("DeepSeek 调用失败", result.Error, InfoBarSeverity.Error);
            }
        }
        finally
        {
            DeepSeekProgress.IsActive = false;
            AskDeepSeekButton.IsEnabled = true;
            RefreshDeepSeekStatus();
        }
    }

    private void ClearSpeaking_Click(object sender, RoutedEventArgs e)
    {
        SpeakingStemBox.Text = string.Empty;
        SpeakingResultBox.Text = string.Empty;
    }

    private void CopySpeakingResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SpeakingResultBox.Text))
        {
            ShowMessage("暂无结果", "先发送一次口语题干，等模型返回后再复制。", InfoBarSeverity.Warning);
            return;
        }
        CopyText(SpeakingResultBox.Text);
        ShowMessage("结果已复制", "模型返回的文本已复制到剪贴板。", InfoBarSeverity.Success);
    }

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        CopyText("http://127.0.0.1:10086/json/list");
        ShowMessage("地址已复制", "调试端口地址已复制到剪贴板。", InfoBarSeverity.Success);
    }

    private async void RefreshPort_Click(object sender, RoutedEventArgs e)
    {
        bool available = await ProbePortAsync();
        ShowMessage(available ? "端口可用" : "端口未连接",
            available ? "127.0.0.1:10086 可以访问。" : "请先启动带调试端口的 ETS。",
            available ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"ets-logs-{DateTime.Now:yyyyMMdd-HHmmss}",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeChoices.Add("ZIP 压缩包", new List<string> { ".zip" });
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException("窗口尚未准备好。");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            int count = await Task.Run(() => WriteLogArchive(file.Path));
            RefreshLogSummary();
            ShowMessage("日志已导出", $"已打包 {count} 个文件：{file.Path}", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage("导出失败", ex.Message, InfoBarSeverity.Error); }
    }

    private int WriteLogArchive(string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        using ZipArchive archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        int count = 0;
        if (Directory.Exists(_service.DataRoot))
        {
            foreach (string file in Directory.EnumerateFiles(_service.DataRoot, "*.log").OrderBy(Path.GetFileName))
            {
                AddFile(archive, file, $"logs/{Path.GetFileName(file)}");
                count++;
            }
        }

        string config = Path.Combine(_service.AppRoot, "ets_answer_config.yaml");
        if (File.Exists(config)) { AddFile(archive, config, "config/ets_answer_config.yaml"); count++; }
        string installation = Path.Combine(_service.DataRoot, "ets_install_dir.txt");
        if (File.Exists(installation)) { AddFile(archive, installation, "config/ets_install_dir.txt"); count++; }

        ZipArchiveEntry entry = archive.CreateEntry("environment.txt", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            writer.Write(BuildEnvironmentText());
        return count + 1;
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream target = entry.Open();
        source.CopyTo(target);
    }

    private void RefreshLogSummary()
    {
        if (!Directory.Exists(_service.DataRoot))
        {
            LogSummaryText.Text = "日志目录不存在。";
            return;
        }
        var files = Directory.EnumerateFiles(_service.DataRoot, "*.log")
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.Name)
            .ToList();
        LogSummaryText.Text = files.Count == 0
            ? $"日志目录：{_service.DataRoot}\n暂无日志文件。"
            : $"日志目录：{_service.DataRoot}\n" +
              string.Join(" · ", files.Select(info => $"{info.Name} {info.Length / 1024.0:0.#} KB"));
    }

    private void CopyEnvironment_Click(object sender, RoutedEventArgs e)
    {
        CopyText(BuildEnvironmentText());
        ShowMessage("环境信息已复制", "当前程序、数据目录和端口信息已复制到剪贴板。", InfoBarSeverity.Success);
    }

    private string BuildEnvironmentText()
    {
        var lines = new List<string>
        {
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"应用目录：{_service.AppRoot}",
            $"数据目录：{_service.DataRoot}",
            $"ETS 程序：{_service.EtsExe ?? "未找到"}",
            $"后台引擎：{_service.EnginePath}",
            $"调试端口：10086（{(_portAvailable ? "可用" : "未连接")}）",
            $"ETS 进程：{(_service.IsEtsRunning ? "运行中" : "未运行")}",
            $"系统：{Environment.OSVersion.VersionString}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(_service.DataRoot);
    private void OpenProgram_Click(object sender, RoutedEventArgs e) => OpenFolder(_service.AppRoot);

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在：" + path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage("无法打开目录", ex.Message, InfoBarSeverity.Error); }
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }
}
