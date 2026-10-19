using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace ETSAssistant.WinUI3.Services;

/// <summary>
/// Reopens InfoBars when changing severity so the icon and severity colors are refreshed together.
/// </summary>
public static class InfoBarPresenter
{
    private sealed class PendingUpdate
    {
        public long Version;
    }

    private static readonly ConditionalWeakTable<InfoBar, PendingUpdate> PendingUpdates = new();

    public static void Show(InfoBar infoBar, string title, string message, InfoBarSeverity severity)
    {
        PendingUpdate pending = PendingUpdates.GetValue(infoBar, static _ => new PendingUpdate());
        long version = ++pending.Version;

        infoBar.IsOpen = false;
        if (!infoBar.DispatcherQueue.TryEnqueue(() =>
        {
            if (pending.Version != version) return;

            infoBar.Title = title;
            infoBar.Message = message;
            infoBar.Severity = severity;
            infoBar.IsOpen = true;
        }))
        {
            infoBar.Title = title;
            infoBar.Message = message;
            infoBar.Severity = severity;
            infoBar.IsOpen = true;
        }
    }
}
