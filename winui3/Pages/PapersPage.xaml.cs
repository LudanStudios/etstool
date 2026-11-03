using System.Diagnostics;
using ETSAssistant.WinUI3.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class PapersPage : Page
{
    private readonly AssistantService _service = AssistantService.Current;
    private readonly PaperCatalog _catalog;
    private readonly AnswerHistoryStore _history;
    private bool _busy;

    public PapersPage()
    {
        _catalog = new PaperCatalog(_service.DataRoot);
        _history = new AnswerHistoryStore(_service.DataRoot);
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在读取本机试卷…");
        try
        {
            IReadOnlyList<PaperEntry> entries = await Task.Run(_catalog.Scan);
            PaperList.ItemsSource = entries;
            SummaryText.Text = $"共 {entries.Count} 份试卷";
            EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { ShowMessage("读取失败", ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        PaperList.IsEnabled = !busy;
        if (busy && message.Length > 0) SummaryText.Text = message;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;
        try
        {
            if (!Directory.Exists(paper.FolderPath))
                throw new DirectoryNotFoundException("试卷目录已经不存在，请刷新列表。");
            Process.Start(new ProcessStartInfo(paper.FolderPath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage("无法打开目录", ex.Message, InfoBarSeverity.Error); }
    }

    private async void AnalyzeAnswers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;
        try
        {
            IReadOnlyList<PaperAnswerItem> answers = await Task.Run(() => _catalog.ReadAnswers(paper.PackId));
            if (answers.Count == 0)
            {
                ShowMessage("没有答案", "这份试卷中没有可分析的选择题答案。", InfoBarSeverity.Warning);
                return;
            }

            var selected = _history.Load().Records
                .Where(record => record.PackId == paper.PackId)
                .GroupBy(record => record.SessionId)
                .OrderByDescending(group => group.Max(record => record.Timestamp))
                .FirstOrDefault()?
                .GroupBy(record => record.QuestionId)
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(record => record.Timestamp).First().SelectedAnswer)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);

            int answered = answers.Count(answer => selected.ContainsKey(answer.QuestionId));
            int correct = answers.Count(answer =>
                selected.TryGetValue(answer.QuestionId, out string? chosen) && chosen == answer.Answer);
            var distribution = answers.GroupBy(answer => answer.Answer)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key} {group.Count()} 题（{group.Count() * 100.0 / answers.Count:0.#}%）");

            var lines = new List<string> { $"试卷 {paper.PackId}（{answers.Count} 道选择题）" };
            lines.Add(answered == 0
                ? "本地作答记录：暂无"
                : $"上次作答：已答 {answered} 题 · 正确 {correct} 题 · 错误 {answered - correct} 题");
            lines.Add("答案分布：" + string.Join(" · ", distribution));
            lines.Add("答案：" + string.Join("  ", answers.Select(answer => $"{answer.Number}.{answer.Answer}")));
            string text = string.Join(Environment.NewLine, lines);

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowMessage("分析已复制", $"已复制 {answers.Count} 道题的答案分布、作答统计和完整答案。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage("无法分析答案", ex.Message, InfoBarSeverity.Error); }
    }

    private async void DetailedAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;
        if (!ApiKeyStore.HasKey)
        {
            if (MainWindow.Current is { } mainWindow)
                await mainWindow.PromptForMissingCredentialsAsync();
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "发送试题到 DeepSeek？",
            Content = $"详细解析（Beta）会将“{paper.Title}”的题干、选项和标准答案发送给 DeepSeek，用于逐题生成讲解。不会发送本机凭据或作答历史。请求由你的个人 API Key 计费。继续？",
            PrimaryButtonText = "确认并继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;

        string targetPath;
        try
        {
            string downloads = PdfAnalysisDocumentService.GetDownloadsFolderPath();
            Directory.CreateDirectory(downloads);
            targetPath = Path.Combine(downloads,
                $"ETS-解析-{paper.PackId}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}.pdf");
        }
        catch (Exception ex)
        {
            ShowMessage("无法访问下载文件夹", ex.Message, InfoBarSeverity.Error);
            return;
        }

        Guid activityId = AnalysisActivityService.Current.Start($"试卷 {paper.PackId} · 详细解析");
        try
        {
            var progress = new Progress<string>(message => AnalysisActivityService.Current.Update(activityId, message));
            string markdown = await new PaperAnalysisService().GenerateAsync(paper, progress);
            AnalysisActivityService.Current.Update(activityId, $"正在排版并保存到：{targetPath}");
            await PdfAnalysisDocumentService.SaveMarkdownAsync(markdown, targetPath);
            AnalysisActivityService.Current.Complete(activityId, $"已保存到：{targetPath}", targetPath);
            if (MainWindow.Current?.IsShowingPage(this) == true)
                await PdfAnalysisDocumentService.ShowPreviewAsync(XamlRoot, targetPath, paper.Title);
            if (MainWindow.Current?.IsShowingPage(this) == true)
                ShowMessage("解析已保存", $"PDF 保存位置：{targetPath}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AnalysisActivityService.Current.Fail(activityId, ex.Message);
            if (MainWindow.Current?.IsShowingPage(this) == true)
                ShowMessage("详细解析失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ExtractAnswers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;
        try
        {
            IReadOnlyList<PaperAnswerItem> answers = await Task.Run(() => _catalog.ReadAnswers(paper.PackId));
            if (answers.Count == 0)
            {
                ShowMessage("没有答案", "这份试卷中没有可读取的选择题答案。", InfoBarSeverity.Warning);
                return;
            }

            string text = string.Join("  ", answers.Select(answer => $"{answer.Number}.{answer.Answer}"));
            var viewer = new ScrollViewer
            {
                MaxHeight = 340,
                Content = new TextBlock
                {
                    Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                }
            };
            var dialog = new ContentDialog
            {
                Title = $"试卷 {paper.PackId} 的答案（{answers.Count} 题）",
                Content = viewer,
                PrimaryButtonText = "复制答案",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowMessage("答案已复制", $"已复制 {answers.Count} 道题的答案。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage("无法提取答案", ex.Message, InfoBarSeverity.Error); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;

        var dialog = new ContentDialog
        {
            Title = "确认删除试卷？",
            Content = BuildDeleteWarning(paper),
            PrimaryButtonText = "确认删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };
        dialog.Opened += (_, _) =>
        {
            FocusCancelButton(dialog);
            if (FindButton(dialog, "确认删除") is Button confirm)
            {
                var danger = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                confirm.Background = danger;
                confirm.BorderBrush = danger;
                confirm.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            }
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await DeletePaperAsync(paper);
    }

    private static void FocusCancelButton(DependencyObject root)
    {
        if (FindButton(root, "取消") is Button cancel) cancel.Focus(FocusState.Programmatic);
    }

    private static Button? FindButton(DependencyObject root, string text)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Button button && string.Equals(button.Content as string, text, StringComparison.Ordinal))
                return button;
            Button? nested = FindButton(child, text);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static FrameworkElement BuildDeleteWarning(PaperEntry paper)
    {
        Color warning = Color.FromArgb(255, 248, 113, 113);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(new SymbolIcon(Symbol.Important) { Foreground = new SolidColorBrush(warning) });
        heading.Children.Add(new TextBlock
        {
            Text = "此操作不可恢复", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(warning)
        });

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = $"试卷编号：{paper.PackId}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = paper.FolderPath, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "将永久删除试卷目录及其转换文件；已记录的答题分析会保留。", TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private async Task DeletePaperAsync(PaperEntry paper)
    {
        try
        {
            SetBusy(true, "正在删除试卷…");
            await Task.Run(() => _catalog.Delete(paper.PackId));
            SetBusy(false);
            await ReloadAsync();
            ShowMessage("试卷已删除", $"已删除试卷 {paper.PackId}，答题分析记录仍然保留。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage("删除失败", ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        InfoBarPresenter.Show(MessageBar, title, message, severity);
    }
}
