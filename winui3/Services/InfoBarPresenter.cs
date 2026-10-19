using System.Runtime.CompilerServices;
using System.Threading;
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
        long version = Interlocked.Increment(ref pending.Version);

        void Apply()
        {
            if (Volatile.Read(ref pending.Version) != version) return;

            infoBar.IsOpen = false;
            infoBar.Title = title;
            infoBar.Message = message;
            infoBar.Severity = severity;
            infoBar.IsOpen = true;
        }

        if (infoBar.DispatcherQueue.HasThreadAccess)
            Apply();
        else
            infoBar.DispatcherQueue.TryEnqueue(Apply);
    }
}
