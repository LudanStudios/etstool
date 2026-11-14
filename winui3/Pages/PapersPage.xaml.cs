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
    private static string T(string key) => LocalizationService.Current.Translate(key);
    private static string F(string key, params (string Name, object? Value)[] values) =>
        LocalizationService.Current.Format(key, values);

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
        SetBusy(true, T("papers.reading_local_papers"));
        try
        {
            IReadOnlyList<PaperEntry> entries = await Task.Run(_catalog.Scan);
            PaperList.ItemsSource = entries;
            SummaryText.Text = F("papers.paper_count", ("count", entries.Count));
            EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { ShowMessage(T("papers.read_failed"), ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        PaperList.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ClearAllButton.IsEnabled = !busy && PaperList.Items.Count > 0;
        if (busy && message.Length > 0) SummaryText.Text = message;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void PaperRow_Loaded(object sender, RoutedEventArgs e)
    {
        // Rows are realized after the page's localization pass, including recycled list rows.
        if (sender is DependencyObject row)
            LocalizationService.Current.Apply(row);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;
        try
        {
            if (!Directory.Exists(paper.FolderPath))
                throw new DirectoryNotFoundException(T("papers.folder_missing_refresh"));
            Process.Start(new ProcessStartInfo(paper.FolderPath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage(T("papers.open_folder_failed"), ex.Message, InfoBarSeverity.Error); }
    }

    private async void AnalyzeAnswers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;
        try
        {
            IReadOnlyList<PaperAnswerItem> answers = await Task.Run(() => _catalog.ReadAnswers(paper.PackId));
            if (answers.Count == 0)
            {
                ShowMessage(T("papers.no_answers_title"), T("papers.no_analyzable_answers"), InfoBarSeverity.Warning);
                return;
            }

            var selected = CurrentPaperService.SelectCurrentRecords(_history.Load().Records, paper.PackId)
                .GroupBy(record => record.QuestionId)
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(record => record.Timestamp).First().SelectedAnswer);

            int answered = answers.Count(answer => selected.ContainsKey(answer.QuestionId));
            int correct = answers.Count(answer =>
                selected.TryGetValue(answer.QuestionId, out string? chosen) && chosen == answer.Answer);
            var distribution = answers.GroupBy(answer => answer.Answer)
                .OrderBy(group => group.Key)
                .Select(group => F("papers.distribution_item", ("answer", group.Key), ("count", group.Count()),
                    ("percent", (group.Count() * 100.0 / answers.Count).ToString("0.#"))));

            var lines = new List<string> { F("papers.answer_analysis_title", ("paperId", paper.PackId), ("count", answers.Count)) };
            lines.Add(answered == 0
                ? T("papers.local_history_empty")
                : F("papers.last_attempt_summary", ("answered", answered), ("correct", correct), ("wrong", answered - correct)));
            lines.Add(F("papers.answer_distribution", ("distribution", string.Join(" · ", distribution))));
            lines.Add(F("papers.answers", ("answers", string.Join("  ", answers.Select(answer => $"{answer.Number}.{answer.Answer}")))));
            string text = string.Join(Environment.NewLine, lines);

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowMessage(T("papers.analysis_copied_title"), F("papers.analysis_copied_message", ("count", answers.Count)),
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(T("papers.analyze_failed"), ex.Message, InfoBarSeverity.Error); }
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
            Title = T("papers.send_questions_deepseek"),
            Content = F("papers.detailed_analysis_confirmation", ("title", paper.Title)),
            PrimaryButtonText = T("papers.confirm_continue"),
            CloseButtonText = T("common.cancel"),
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
                $"{T("papers.analysis_file_prefix")}-{paper.PackId}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}.pdf");
        }
        catch (Exception ex)
        {
            ShowMessage(T("papers.download_folder_unavailable"), ex.Message, InfoBarSeverity.Error);
            return;
        }

        Guid activityId = AnalysisActivityService.Current.Start(F("papers.detailed_analysis_activity_title", ("paperId", paper.PackId)));
        try
        {
            var progress = new Progress<string>(message => AnalysisActivityService.Current.Update(activityId, message));
            string markdown = await new PaperAnalysisService().GenerateAsync(paper, progress);
            AnalysisActivityService.Current.Update(activityId, F("papers.layout_saving_path", ("path", targetPath)));
            await PdfAnalysisDocumentService.SaveMarkdownAsync(markdown, targetPath);
            AnalysisActivityService.Current.Complete(activityId, F("papers.saved_to_path", ("path", targetPath)), targetPath);
            if (MainWindow.Current?.IsShowingPage(this) == true)
                await PdfAnalysisDocumentService.ShowPreviewAsync(XamlRoot, targetPath, paper.Title);
            if (MainWindow.Current?.IsShowingPage(this) == true)
                ShowMessage(T("papers.analysis_saved_title"), F("papers.pdf_saved_location", ("path", targetPath)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AnalysisActivityService.Current.Fail(activityId, ex.Message);
            if (MainWindow.Current?.IsShowingPage(this) == true)
                ShowMessage(T("papers.detailed_analysis_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ExtractAnswers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;
        try
        {
            IReadOnlyList<PaperAnswerItem> answers = await Task.Run(() => _catalog.ReadAnswers(paper.PackId));
            string text = PaperCopyService.Format(paper.PackId, answers.Select(a => (a.Number, a.Answer)), _service.DataRoot, _service.AppRoot);
            int answerCount = answers.Count + new SpeakingAnswerService(_service.DataRoot).Load(paper.PackId).Count;
            if (text.Length == 0)
            {
                ShowMessage(T("papers.no_answers_title"), T("papers.no_extractable_answers"), InfoBarSeverity.Warning);
                return;
            }

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
                Title = F("papers.paper_answers_title", ("paperId", paper.PackId), ("count", answerCount)),
                Content = viewer,
                PrimaryButtonText = T("papers.copy_answers"),
                CloseButtonText = T("papers.close"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowMessage(T("papers.answers_copied_title"), F("papers.answers_copied", ("count", answerCount)), InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(T("papers.extract_answers_failed"), ex.Message, InfoBarSeverity.Error); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaperEntry paper) return;

        var dialog = new ContentDialog
        {
            Title = T("papers.delete_confirmation_title"),
            Content = BuildDeleteWarning(paper),
            PrimaryButtonText = T("papers.confirm_delete"),
            CloseButtonText = T("papers.cancel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };
        dialog.Opened += (_, _) =>
        {
            FocusCancelButton(dialog);
            if (FindButton(dialog, T("papers.confirm_delete")) is Button confirm)
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

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        DiagnosticLogService.Info("papers.clear_all.button.pressed");

        IReadOnlyList<PaperEntry> papers;
        try { papers = await Task.Run(_catalog.Scan); }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("papers.clear_all.scan.failed", ex);
            ShowMessage(T("papers.read_failed"), ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (papers.Count == 0)
        {
            ShowMessage(T("papers.clear_all_none_title"), T("papers.clear_all_none_detail"), InfoBarSeverity.Informational);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = T("papers.clear_all_confirmation_title"),
            Content = BuildClearAllWarning(papers.Count),
            PrimaryButtonText = T("papers.clear_all_confirm"),
            CloseButtonText = T("papers.cancel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };
        dialog.Opened += (_, _) =>
        {
            FocusCancelButton(dialog);
            if (FindButton(dialog, T("papers.clear_all_confirm")) is Button confirm)
            {
                var danger = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                confirm.Background = danger;
                confirm.BorderBrush = danger;
                confirm.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            }
        };
        if (await DiagnosticLogService.ShowDialogAsync(dialog, "papers.clear_all.confirmation") != ContentDialogResult.Primary)
            return;

        DiagnosticLogService.Info("papers.clear_all.started", $"confirmedCount={papers.Count}");
        SetBusy(true, T("papers.clear_all_in_progress"));
        try
        {
            (int deleted, List<string> failures) = await Task.Run(() =>
            {
                int removed = 0;
                var failed = new List<string>();
                foreach (PaperEntry paper in _catalog.Scan())
                {
                    try
                    {
                        _catalog.Delete(paper.PackId);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add(paper.PackId);
                        DiagnosticLogService.Warn("papers.clear_all.paper.failed", $"paperId={paper.PackId} detail={ex.Message}");
                    }
                }
                return (removed, failed);
            });

            SetBusy(false);
            await ReloadAsync();
            if (failures.Count == 0)
            {
                DiagnosticLogService.Info("papers.clear_all.completed", $"deleted={deleted}");
                ShowMessage(T("papers.clear_all_completed_title"),
                    F("papers.clear_all_completed_detail", ("count", deleted)), InfoBarSeverity.Success);
            }
            else
            {
                DiagnosticLogService.Warn("papers.clear_all.partial", $"deleted={deleted} failed={failures.Count}");
                ShowMessage(T("papers.clear_all_partial_title"),
                    F("papers.clear_all_partial_detail", ("deleted", deleted), ("failed", failures.Count),
                        ("paperIds", string.Join(", ", failures))), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("papers.clear_all.failed", ex);
            ShowMessage(T("papers.clear_all_failed"), ex.Message, InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private static FrameworkElement BuildClearAllWarning(int count)
    {
        Color warning = Color.FromArgb(255, 248, 113, 113);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(new SymbolIcon(Symbol.Important) { Foreground = new SolidColorBrush(warning) });
        heading.Children.Add(new TextBlock
        {
            Text = T("papers.delete_irreversible"), FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(warning)
        });

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = F("papers.clear_all_confirmation_message", ("count", count)),
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private static void FocusCancelButton(DependencyObject root)
    {
        if (FindButton(root, T("papers.cancel")) is Button cancel) cancel.Focus(FocusState.Programmatic);
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
            Text = T("papers.delete_irreversible"), FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(warning)
        });

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = F("papers.paper_number", ("paperId", paper.PackId)), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = paper.FolderPath, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = T("papers.delete_explanation"), TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private async Task DeletePaperAsync(PaperEntry paper)
    {
        try
        {
            SetBusy(true, T("papers.deleting"));
            await Task.Run(() => _catalog.Delete(paper.PackId));
            SetBusy(false);
            await ReloadAsync();
            ShowMessage(T("papers.deleted_title"), F("papers.deleted_detail", ("paperId", paper.PackId)), InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(T("papers.delete_failed"), ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        InfoBarPresenter.Show(MessageBar, title, message, severity);
    }
}
