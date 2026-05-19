using Avalonia.Media;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels;

/// <summary>
/// Lifetime handle returned by MainWindowViewModel when a test-report modal is opened.
/// Provides the <see cref="IProgress{TestReportStep}"/> sink to stream timeline steps,
/// and flips the spinner off when disposed.
/// </summary>
public sealed class TestReportSession : IDisposable
{
    private readonly Action _onDispose;
    private bool _disposed;

    public TestReportSession(IProgress<TestReportStep> progress, Action onDispose)
    {
        Progress = progress;
        _onDispose = onDispose;
    }

    public IProgress<TestReportStep> Progress { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _onDispose();
    }
}

public enum TestReportStepStatus
{
    Info,
    Running,
    Ok,
    Fail
}

public sealed class TestReportStep : ObservableObject
{
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#81C784"));
    private static readonly IBrush FailBrush = new SolidColorBrush(Color.Parse("#F1A18F"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#90A4AE"));

    private string _title = string.Empty;
    private string _detail = string.Empty;
    private TestReportStepStatus _status = TestReportStepStatus.Info;

    public TestReportStep() { }

    public TestReportStep(string title, string detail, TestReportStepStatus status)
    {
        _title = title;
        _detail = detail;
        _status = status;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public TestReportStepStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                NotifyPropertyChanged(nameof(StatusIcon));
                NotifyPropertyChanged(nameof(StatusBrush));
                NotifyPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public string StatusIcon => _status switch
    {
        TestReportStepStatus.Ok => "✓",      // ✓
        TestReportStepStatus.Fail => "✗",    // ✗
        TestReportStepStatus.Running => "○", // ○
        _ => "•"                              // •
    };

    public string StatusLabel => _status switch
    {
        TestReportStepStatus.Ok => "ok",
        TestReportStepStatus.Fail => "fail",
        TestReportStepStatus.Running => "…",
        _ => "info"
    };

    public IBrush StatusBrush => _status switch
    {
        TestReportStepStatus.Ok => OkBrush,
        TestReportStepStatus.Fail => FailBrush,
        TestReportStepStatus.Running => RunningBrush,
        _ => InfoBrush
    };
}
