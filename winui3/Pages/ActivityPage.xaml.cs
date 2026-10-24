using ETSAssistant.WinUI3.Services;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class ActivityPage : Page
{
    private readonly AnalysisActivityService _activities = AnalysisActivityService.Current;

    public ActivityPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _activities.Changed += OnActivitiesChanged;
            Refresh();
        };
        Unloaded += (_, _) => _activities.Changed -= OnActivitiesChanged;
    }

    private void OnActivitiesChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(Refresh);
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        AnalysisActivity[] items = _activities.Snapshot().ToArray();
        AnalysisActivity[] running = items.Where(activity => activity.IsRunning).ToArray();
        AnalysisActivity[] completed = items.Where(activity => !activity.IsRunning).Take(8).ToArray();
        bool hasRunningActivities = running.Length > 0;
        HeaderRing.IsActive = hasRunningActivities;
        HeaderRing.Visibility = hasRunningActivities ? Visibility.Visible : Visibility.Collapsed;
        HeaderSyncIcon.Visibility = hasRunningActivities ? Visibility.Collapsed : Visibility.Visible;
        RunningItems.ItemsSource = running;
        CompletedItems.ItemsSource = completed;
        EmptyText.Visibility = running.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        string runningText = LocalizationService.Current.Translate("正在进行");
        RunningHeader.Text = running.Length == 0 ? runningText : $"{runningText} ({running.Length})";
        CompletedHeader.Visibility = completed.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CompletedItems.Visibility = completed.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void PreviewPdf_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AnalysisActivity activity ||
            string.IsNullOrWhiteSpace(activity.OutputPath))
            return;
        try { await PdfAnalysisDocumentService.ShowPreviewAsync(XamlRoot, activity.OutputPath, activity.Title); }
        catch (Exception ex) { ShowError("无法预览 PDF", ex.Message); }
    }

    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AnalysisActivity activity ||
            string.IsNullOrWhiteSpace(activity.OutputPath))
            return;
        try { PdfAnalysisDocumentService.OpenWithDefaultProgram(activity.OutputPath); }
        catch (Exception ex) { ShowError("无法打开 PDF", ex.Message); }
    }

    private void ShowError(string title, string message) =>
        InfoBarPresenter.Show(ActivityInfo, title, message, InfoBarSeverity.Error);
}
