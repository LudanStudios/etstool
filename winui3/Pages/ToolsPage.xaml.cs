using System.ComponentModel;
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
    private readonly AliyunTtsService _aliyunTts = new(AssistantService.Current.AppRoot);
    private readonly SpeakingSessionStore _sessionStore = new(AssistantService.Current.AppRoot);
    private readonly VirtualCableService _cable = new();
    private SpeakingSession? _session;
    private int _sessionIndex;
    private string? _speakingPackId;
    private IReadOnlyList<SpeakingPaper> _speakingPapers = Array.Empty<SpeakingPaper>();
    private string? _lastTtsWavPath;
    private readonly SpeakingAutoRunner _autoSpeakingRunner = new();
    private bool _portAvailable;
    private bool _updatingCapture;
    private bool _updatingVerbose;

    public ToolsPage()
    {
        InitializeComponent();
        _capture.StatusChanged += OnCaptureStatusChanged;
        _cable.PlaybackEnded += OnCablePlaybackEnded;
        _autoSpeakingRunner.Output += ReportAutoSpeaking;
        _autoSpeakingRunner.Exited += () => DispatcherQueue.TryEnqueue(() =>
        {
            AutoSpeakingButtonText.Text = "开始自动口语";
            AutoSpeakingStatusText.Text = "自动口语已结束。";
        });
        Unloaded += (_, _) =>
        {
            _capture.StatusChanged -= OnCaptureStatusChanged;
            _cable.PlaybackEnded -= OnCablePlaybackEnded;
        };
        Loaded += async (_, _) =>
        {
            RefreshLogSummary();
            RefreshDeepSeekStatus();
            RefreshAliyunTtsStatus();
            RefreshAudioStatus();
            RefreshSessionUi();
            UpdateSpeakingModeUi();
            UpdateCaptureUi();
            _updatingVerbose = true;
            VerboseLoggingToggle.IsOn = AppPreferencesService.Current.VerboseLoggingEnabled;
            _updatingVerbose = false;
            EnvironmentText.Text = BuildEnvironmentText();
            await RefreshCableAsync();
            await RefreshDefaultMicStatusAsync();
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

    private void VerboseLoggingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingVerbose) return;
        AppPreferencesService.Current.SetVerboseLogging(VerboseLoggingToggle.IsOn);
        ShowMessage("详细日志已更新", VerboseLoggingToggle.IsOn
            ? "后台监听器会在日志中附加听力匹配和 Worker 诊断细节。"
            : "后台监听器已关闭详细级别日志。", InfoBarSeverity.Informational);
    }

    private async Task RefreshDefaultMicStatusAsync()
    {
        DefaultAudioInputStatus? status = await DefaultAudioInputService.Current.GetStatusAsync();
        DefaultMicStatusText.Text = status is null
            ? "系统默认输入：无法读取"
            : $"系统默认输入：{status.Name}{(status.IsVirtualCable ? "（CABLE Output）" : "（物理/其他设备）")}";
    }

    private async void SetVirtualMicDefault_Click(object sender, RoutedEventArgs e)
    {
        string result = await DefaultAudioInputService.Current.SetVirtualDefaultAsync();
        await RefreshDefaultMicStatusAsync();
        ShowMessage(result.StartsWith("已设为", StringComparison.Ordinal) ? "虚拟麦克风已启用" : "无法设置虚拟麦克风",
            result, result.StartsWith("已设为", StringComparison.Ordinal) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void RestoreDefaultMic_Click(object sender, RoutedEventArgs e)
    {
        string result = await DefaultAudioInputService.Current.RestorePreviousAsync();
        await RefreshDefaultMicStatusAsync();
        ShowMessage("默认输入状态", result, InfoBarSeverity.Informational);
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

    private void RefreshAudioStatus()
    {
        var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
        string state = audio.HasInput ? $"已准备：{audio.InputPath}" : $"尚未准备 WAV（目标：{audio.InputPath}）";
        AudioStatusText.Text = state + (_service.IsEtsRunning ? " · ETS 正在运行" : " · ETS 未运行");
    }

    private async void PickSpeakingWav_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add(".wav");
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException("窗口尚未准备好。");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
            string output = await audio.PrepareAsync(file.Path);
            RefreshAudioStatus();
            ShowMessage("WAV 已准备", $"已转换为 16-bit / 48 kHz：{output}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("准备 WAV 失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void UseLastTtsWav_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastTtsWavPath) || !File.Exists(_lastTtsWavPath))
        {
            ShowMessage("没有可用的 TTS WAV", "先在下面的阿里云 TTS 区域合成一次语音。",
                InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
            string output = await audio.PrepareAsync(_lastTtsWavPath);
            RefreshAudioStatus();
            ShowMessage("TTS WAV 已准备", $"已转换为 16-bit / 48 kHz：{output}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("准备 TTS WAV 失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void LaunchEtsWithFakeMic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
            if (!audio.HasInput)
            {
                ShowMessage("还没有 WAV", "先点“选择 WAV 并准备”，再启动 ETS。", InfoBarSeverity.Warning);
                return;
            }
            if (_service.IsEtsRunning)
            {
                ShowMessage("ETS 正在运行", "请先完全关闭 ETS（包括托盘里的进程），再用虚拟麦克风启动。",
                    InfoBarSeverity.Warning);
                return;
            }

            bool loop = LoopAudioCheck.IsChecked == true;
            _service.OpenEts(audio.BuildFakeMicrophoneArguments(loop));
            RefreshAudioStatus();
            ShowMessage("已请求启动 ETS",
                "如果出现 UAC 提示请允许。启动后进入口语题，ETS 录音时会读取这个 WAV。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("启动失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnCablePlaybackEnded() =>
        DispatcherQueue.TryEnqueue(() => SessionStatusText.Text = "播放完成。");

    private async Task RefreshCableAsync()
    {
        try
        {
            VirtualCableDevice? device = await _cable.RefreshAsync();
            if (_cable.IsReady)
            {
                CableStatusText.Text = $"虚拟声卡：{_cable.Current!.Name} → {_cable.Capture!.Name}";
            }
            else if (_cable.IsAvailable)
            {
                CableStatusText.Text =
                    $"虚拟声卡：{_cable.Current!.Name}；未检测到 CABLE Output。打开录音设备，启用 CABLE Output 并把 ETS 麦克风设为它。";
            }
            else
            {
                CableStatusText.Text = "虚拟声卡：未检测到 CABLE Input。点“安装/配置虚拟声卡”安装 VB-CABLE。";
            }
        }
        catch (Exception ex)
        {
            CableStatusText.Text = "虚拟声卡：检测失败 — " + ex.Message;
        }
    }

    private async void RefreshCable_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCableAsync();
        if (_cable.IsReady)
            ShowMessage("虚拟声卡已就绪", $"{_cable.Current!.Name} → {_cable.Capture!.Name}", InfoBarSeverity.Success);
        else if (_cable.IsAvailable)
            ShowMessage("缺少 CABLE Output", "CABLE Input 已安装，请启用录音端的 CABLE Output。", InfoBarSeverity.Warning);
        else
            ShowMessage("未检测到虚拟声卡", "请点“安装/配置虚拟声卡”安装 VB-CABLE。", InfoBarSeverity.Warning);
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage("无法打开声音设置", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void VirtualCableSetup_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCableAsync();
        if (_cable.IsReady)
        {
            ShowMessage("虚拟声卡已就绪",
                $"{_cable.Current!.Name} → {_cable.Capture!.Name}。请在 ETS 里把麦克风设为 CABLE Output。",
                InfoBarSeverity.Success);
            VirtualCableSetupService.OpenRecordingDevices();
            return;
        }
        if (_cable.IsAvailable)
        {
            ShowMessage("缺少 CABLE Output",
                "CABLE Input 已安装，但录音端 CABLE Output 没有启用。打开录音设备，右键显示禁用设备，启用 CABLE Output，再把它设为 ETS 麦克风。",
                InfoBarSeverity.Warning);
            VirtualCableSetupService.OpenRecordingDevices();
            return;
        }

        string installer = VirtualCableSetupService.InstallerPath(_service.AppRoot);
        if (!File.Exists(installer))
        {
            ShowMessage("找不到 VB-CABLE 安装包", $"缺少 {installer}", InfoBarSeverity.Error);
            return;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "将运行随程序附带的 VB-CABLE 安装程序。需要管理员权限，安装完成后建议重启电脑。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"安装包：{installer}",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            Title = "安装 VB-CABLE 虚拟声卡",
            Content = content,
            PrimaryButtonText = "开始安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            ShowMessage("正在安装", "请在安装程序中允许管理员权限，完成后回到这里。",
                InfoBarSeverity.Informational);
            await VirtualCableSetupService.RunInstallerAsync(_service.AppRoot);
            await RefreshCableAsync();
            if (_cable.IsReady)
            {
                ShowMessage("安装完成", "虚拟声卡已就绪。请在 ETS 里把麦克风设为 CABLE Output。",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage("安装完成，需要配置",
                    "如果声音设备里还没有 CABLE Output，请重启电脑后再打开录音设备启用它，并把 ETS 麦克风设为 CABLE Output。",
                    InfoBarSeverity.Warning);
            }
            VirtualCableSetupService.OpenRecordingDevices();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowMessage("已取消安装", "没有获得管理员权限，VB-CABLE 未安装。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowMessage("安装失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RefreshSessionUi()
    {
        _session ??= _sessionStore.Load();
        SessionItemBox.ItemsSource = _session?.Items;
        if (_session is null)
        {
            SessionStatusText.Text = "尚未预生成整卷音频。";
            return;
        }
        SessionStatusText.Text = $"试卷 {_session.PackId} · {_session.Items.Count} 条音频 · {_session.Mode}";
        if (_session.Items.Count > 0)
        {
            SessionItemBox.SelectedIndex = 0;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (SessionItemBox.SelectedIndex < 0) SessionItemBox.SelectedIndex = 0;
            });
        }
    }

    private SpeakingSessionItem? SelectedSessionItem() =>
        SessionItemBox.SelectedItem as SpeakingSessionItem ?? _session?.Items.FirstOrDefault();

    private async void GenerateSession_Click(object sender, RoutedEventArgs e)
    {
        if (SpeakingPaperBox.SelectedItem is not SpeakingPaper paper)
        {
            ShowMessage("未选择试卷", "先点“读取所有试卷口语题”，在试卷下拉框里选一份试卷。",
                InfoBarSeverity.Warning);
            return;
        }
        if (paper.Questions.Count == 0)
        {
            ShowMessage("没有口语题", "这份试卷里没有可生成的口语题。", InfoBarSeverity.Warning);
            return;
        }
        if (!AliyunTtsCredentialStore.HasCredentials &&
            !AppPreferencesService.Current.ExperienceModeEnabled)
        {
            ShowMessage("未配置阿里云 TTS 凭证",
                "请填写阿里云 TTS 个人凭证，或在偏好设置中启用体验模式，以使用有每日限额的公用 Worker。",
                InfoBarSeverity.Warning);
            return;
        }

        GenerateSessionButton.IsEnabled = false;
        BatchProgress.Visibility = Visibility.Visible;
        BatchProgress.Minimum = 0;
        BatchProgress.Maximum = paper.Questions.Count;
        BatchProgress.Value = 0;
        string mode = AppPreferencesService.Current.SpeakingMode;
        var items = new List<SpeakingSessionItem>();
        var errors = new List<string>();
        int failed = 0;
        try
        {
            for (int index = 0; index < paper.Questions.Count; index++)
            {
                SpeakingQuestion question = paper.Questions[index];
                BatchStatusText.Text = $"正在处理 {index + 1}/{paper.Questions.Count}：{question.Type}";
                string answer = question.StandardAnswer;
                if (mode == "flexible")
                {
                    DeepSeekResult generated = await _deepSeek.AskSpeakingAsync(question.RequestText);
                    if (generated.Success && generated.Text.Length > 0) answer = generated.Text;
                }
                if (string.IsNullOrWhiteSpace(answer))
                {
                    failed++;
                    errors.Add($"第 {question.Number} 题：没有可用答案");
                    BatchProgress.Value = index + 1;
                    continue;
                }

                AliyunTtsResult tts = await _aliyunTts.SynthesizeToWavAsync(answer);
                if (!tts.Success)
                {
                    failed++;
                    errors.Add($"第 {question.Number} 题：{tts.Error}");
                    BatchProgress.Value = index + 1;
                    continue;
                }
                items.Add(new SpeakingSessionItem(question.Number, question.Type, question.Prompt,
                    answer, tts.FilePath));
                BatchProgress.Value = index + 1;
            }

            _session = new SpeakingSession(paper.PackId, mode, DateTimeOffset.Now, items);
            _sessionStore.Save(_session);
            _sessionIndex = 0;
            RefreshSessionUi();
            BatchStatusText.Text = $"完成：{items.Count} 条音频" + (failed > 0 ? $"，{failed} 条失败" : "");
            string detail = errors.Count > 0
                ? Environment.NewLine + string.Join(Environment.NewLine, errors.Take(3))
                : "";
            BatchStatusText.Text += detail;
            ShowMessage("整卷 TTS 已生成",
                $"试卷 {paper.PackId}：{items.Count} 条音频" + (failed > 0 ? $"，{failed} 条失败" : "") + detail,
                failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowMessage("整卷 TTS 失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            GenerateSessionButton.IsEnabled = true;
            BatchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void PlaySessionItem_Click(object sender, RoutedEventArgs e)
    {
        SpeakingSessionItem? item = SelectedSessionItem();
        if (item is null)
        {
            ShowMessage("没有选中音频", "先从播放列表里选一条。", InfoBarSeverity.Warning);
            return;
        }
        PlaySessionItem(item);
    }

    private void PreviewSessionItem_Click(object sender, RoutedEventArgs e)
    {
        SpeakingSessionItem? item = SelectedSessionItem();
        if (item is null)
        {
            ShowMessage("没有可试听的音频", "先从播放列表里选一条。", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            _cable.PlayPreview(item.WavPath);
            SessionStatusText.Text = $"本地试听第 {item.Number} 题：{item.WavName}";
        }
        catch (Exception ex)
        {
            ShowMessage("试听失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void PlayNextSessionItem_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.Items.Count == 0)
        {
            ShowMessage("没有播放列表", "先预生成整卷 TTS。", InfoBarSeverity.Warning);
            return;
        }
        int index = Math.Clamp(_sessionIndex, 0, _session.Items.Count - 1);
        PlaySessionItem(_session.Items[index]);
        _sessionIndex = (index + 1) % _session.Items.Count;
    }

    private void PlaySessionItem(SpeakingSessionItem item)
    {
        try
        {
            _cable.Play(item.WavPath);
            SessionItemBox.SelectedItem = item;
            SessionStatusText.Text = $"正在播放第 {item.Number} 题：{item.WavName}";
        }
        catch (Exception ex)
        {
            ShowMessage("播放失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void StopSessionPlayback_Click(object sender, RoutedEventArgs e)
    {
        _cable.Stop();
        _cable.StopPreview();
        SessionStatusText.Text = "已停止播放。";
    }

    private void RefreshDeepSeekStatus()
    {
        string keyState = ApiKeyStore.HasKey
            ? "API Key：本机已配置（直连）"
            : AppPreferencesService.Current.ExperienceModeEnabled
                ? "API Key：启用体验模式后使用公用 Worker（有每日限额）"
                : "API Key：未配置；体验模式已关闭";
        string attachment = _deepSeek.FindAttachment() ?? "未找到 llm_stt.md";
        string mode = AppPreferencesService.Current.SpeakingMode == "flexible" ? "生成模式" : "标答模式";
        DeepSeekStatusText.Text = $"{mode} · {keyState} · 模型 {DeepSeekService.Model} · 附件：{attachment}";
    }

    private void RefreshAliyunTtsStatus()
    {
        string status = string.Join(Environment.NewLine,
            AliyunTtsCredentialStore.ConfigurationStatus,
            AliyunTtsCredentialStore.CredentialSourceStatus);
        AliyunTtsStatusText.Text = status;
    }

    private void UpdateSpeakingModeUi()
    {
        bool flexible = AppPreferencesService.Current.SpeakingMode == "flexible";
        SpeakingModeSummaryText.Text = $"当前模式：{(flexible ? "生成模式" : "标答模式")}（在答题设置中修改）";
        AskDeepSeekText.Text = flexible ? "发送到 DeepSeek" : "提取标答";
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
            ShowMessage("这道题没有标答", "试卷里没有为这道口语题提供推荐答案，请改用生成模式。",
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
                string source = result.UsedWorker
                    ? $"内置 Worker，今日剩余 {Math.Max(0, result.RemainingToday ?? 0)} 次"
                    : "本机 API Key";
                ShowMessage("DeepSeek 已返回", $"模型 {DeepSeekService.Model} · {source} · 返回 {result.Text.Length} 个字符。",
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

    private async void AliyunTts_Click(object sender, RoutedEventArgs e)
    {
        string text = SpeakingResultBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowMessage("没有可合成的回答", "先提取标答或调用 DeepSeek 生成一条回答。", InfoBarSeverity.Warning);
            return;
        }

        AliyunTtsButton.IsEnabled = false;
        AliyunTtsProgress.IsActive = true;
        try
        {
            AliyunTtsResult result = await _aliyunTts.SynthesizeToWavAsync(text);
            if (!result.Success)
            {
                ShowMessage("阿里云 TTS 失败", result.Error, InfoBarSeverity.Error);
                return;
            }

            AliyunTtsStatusText.Text = $"WAV 已保存：{result.FilePath}";
            _lastTtsWavPath = result.FilePath;
            Process.Start(new ProcessStartInfo(result.FilePath) { UseShellExecute = true });
            string source = result.UsedWorker
                ? $"内置 Worker，今日剩余 {Math.Max(0, result.RemainingToday ?? 0)} 次"
                : "本机阿里云凭据";
            ShowMessage("阿里云 TTS 已完成",
                $"Betty 已合成并打开 WAV 文件（{result.ChunkCount} 段）；{source}。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("阿里云 TTS 失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AliyunTtsProgress.IsActive = false;
            AliyunTtsButton.IsEnabled = true;
            RefreshAliyunTtsStatusIfNoOutput();
        }
    }

    private void RefreshAliyunTtsStatusIfNoOutput()
    {
        if (string.IsNullOrWhiteSpace(AliyunTtsStatusText.Text))
            AliyunTtsStatusText.Text = string.Join(Environment.NewLine,
                AliyunTtsCredentialStore.ConfigurationStatus,
                AliyunTtsCredentialStore.CredentialSourceStatus);
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
            AliyunTtsCredentialStore.ConfigurationStatus,
            AliyunTtsCredentialStore.CredentialSourceStatus,
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

    private async void FullReset_Click(object sender, RoutedEventArgs e)
    {
        var content = new TextBlock
        {
            Text = "即将执行：\n" +
                   "• 停止监听器、转换器和答题进程\n" +
                   "• 移除登录启动项和桌面/启动快捷方式\n" +
                   "• 清除 DeepSeek API Key 和阿里云 TTS 凭证\n" +
                   "• 恢复默认答题设置并清除界面偏好\n" +
                   "• 删除虚拟麦克风 WAV、TTS 临时文件和助手日志\n\n" +
                   "不会删除：试卷包、答题历史、答案记录。",
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = new ContentDialog
        {
            Title = "完全重置？",
            Content = content,
            PrimaryButtonText = "确认重置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var service = new FullResetService(_service);
            FullResetResult result = await service.RunAsync();
            RefreshLogSummary();
            RefreshDeepSeekStatus();
            RefreshAliyunTtsStatus();
            RefreshAudioStatus();
            EnvironmentText.Text = BuildEnvironmentText();
            ShowMessage("完全重置完成",
                string.Join(Environment.NewLine, result.Steps.Concat(result.Notes)),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("完全重置失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void AutoSpeaking_Click(object sender, RoutedEventArgs e)
    {
        if (_autoSpeakingRunner.IsRunning)
        {
            _autoSpeakingRunner.Stop();
            AutoSpeakingStatusText.Text = "正在停止自动口语…";
            return;
        }
        StartAutoSpeaking();
    }

    private async void StartAutoSpeaking()
    {
        _session ??= _sessionStore.Load();
        if (_session is null || _session.Items.Count == 0)
        {
            ShowMessage("没有口语会话", "先预生成整卷 TTS，等播放列表里出现音频后再启动自动口语。",
                InfoBarSeverity.Warning);
            return;
        }
        string[] missing = _session.Items
            .Where(item => !File.Exists(item.WavPath))
            .Select(item => item.WavName)
            .ToArray();
        if (missing.Length > 0)
        {
            ShowMessage("音频文件缺失",
                "这些 TTS 文件已经不在了，请重新预生成整卷 TTS：" + Environment.NewLine + string.Join(Environment.NewLine, missing),
                InfoBarSeverity.Warning);
            return;
        }
        await RefreshCableAsync();
        if (!_cable.IsReady)
        {
            ShowMessage("虚拟声卡未就绪",
                _cable.IsAvailable
                    ? "检测到 CABLE Input，但缺少 CABLE Output。点“安装/配置虚拟声卡”打开录音设备并启用它。"
                    : "未安装 VB-CABLE。点“安装/配置虚拟声卡”安装后再启动自动口语。",
                InfoBarSeverity.Warning);
            return;
        }

        if (!_autoSpeakingRunner.Start(_service.AppRoot, _service.DataRoot, _sessionStore.FilePath, out string error))
        {
            ShowMessage("自动口语启动失败", error, InfoBarSeverity.Error);
            return;
        }
        AutoSpeakingButtonText.Text = "停止自动口语";
        AutoSpeakingStatusText.Text = "自动口语运行中，请保持 ETS 停在口语题页面…";
    }

    private void ReportAutoSpeaking(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        DispatcherQueue.TryEnqueue(() => AutoSpeakingStatusText.Text = line.Trim());
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        InfoBarPresenter.Show(MessageBar, title, message, severity);
    }
}
