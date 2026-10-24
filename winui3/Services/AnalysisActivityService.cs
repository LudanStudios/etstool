using Microsoft.UI.Xaml;

namespace ETSAssistant.WinUI3.Services;

public enum AnalysisActivityState
{
    Running,
    Completed,
    Failed
}

public sealed record AnalysisActivity(
    Guid Id,
    string Title,
    string Detail,
    AnalysisActivityState State,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? OutputPath)
{
    public bool IsRunning => State == AnalysisActivityState.Running;
    public bool IsFailed => State == AnalysisActivityState.Failed;
    public string StateText => LocalizationService.Current.Translate(State switch
    {
        AnalysisActivityState.Running => "进行中",
        AnalysisActivityState.Completed => "已完成",
        _ => "失败"
    });
    public string TimeText => UpdatedAt.ToLocalTime().ToString("HH:mm:ss");
    public Visibility OutputActionsVisibility => string.IsNullOrWhiteSpace(OutputPath)
        ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class AnalysisActivityService
{
    private static readonly Lazy<AnalysisActivityService> Lazy = new(() => new AnalysisActivityService());
    public static AnalysisActivityService Current => Lazy.Value;

    private readonly object _gate = new();
    private readonly List<AnalysisActivity> _activities = new();

    public event Action? Changed;

    public IReadOnlyList<AnalysisActivity> Snapshot()
    {
        lock (_gate)
            return _activities.OrderByDescending(activity => activity.IsRunning)
                .ThenByDescending(activity => activity.UpdatedAt).ToArray();
    }

    public int RunningCount
    {
        get { lock (_gate) return _activities.Count(activity => activity.IsRunning); }
    }

    public Guid Start(string title)
    {
        var now = DateTimeOffset.Now;
        var activity = new AnalysisActivity(Guid.NewGuid(), title, "正在准备请求…",
            AnalysisActivityState.Running, now, now, null);
        lock (_gate) _activities.Add(activity);
        OnChanged();
        return activity.Id;
    }

    public void Update(Guid id, string detail) => Change(id, activity => activity with
    {
        Detail = detail,
        UpdatedAt = DateTimeOffset.Now
    });

    public void Complete(Guid id, string detail, string? outputPath = null) => Change(id, activity => activity with
    {
        Detail = detail,
        State = AnalysisActivityState.Completed,
        UpdatedAt = DateTimeOffset.Now,
        OutputPath = outputPath
    });

    public void Fail(Guid id, string detail) => Change(id, activity => activity with
    {
        Detail = detail,
        State = AnalysisActivityState.Failed,
        UpdatedAt = DateTimeOffset.Now
    });

    private void Change(Guid id, Func<AnalysisActivity, AnalysisActivity> update)
    {
        lock (_gate)
        {
            int index = _activities.FindIndex(activity => activity.Id == id);
            if (index < 0) return;
            _activities[index] = update(_activities[index]);
            if (_activities.Count > 40)
            {
                foreach (AnalysisActivity old in _activities.Where(activity => !activity.IsRunning)
                             .OrderBy(activity => activity.UpdatedAt).Take(_activities.Count - 40).ToArray())
                    _activities.Remove(old);
            }
        }
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke();
}
