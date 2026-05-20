using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Perelegans.ViewModels;

public sealed class UsageStatsSnapshot(
    string title,
    string subtitle,
    string totalText,
    string insightText,
    IReadOnlyList<UsageStatsSliceViewModel> slices,
    IReadOnlyList<UsageTimelineRowViewModel>? timelineRows = null,
    IReadOnlyList<UsageTimelineAxisLabelViewModel>? timelineAxisLabels = null)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string TotalText { get; } = totalText;
    public string InsightText { get; } = insightText;
    public IReadOnlyList<UsageStatsSliceViewModel> Slices { get; } = slices;
    public IReadOnlyList<UsageTimelineRowViewModel> TimelineRows { get; } = timelineRows ?? [];
    public IReadOnlyList<UsageTimelineAxisLabelViewModel> TimelineAxisLabels { get; } = timelineAxisLabels ?? [];
    public bool HasSlices => Slices.Count > 0;
    public bool HasTimelineRows => TimelineRows.Count > 0;
    public double TimelineChartHeight => Math.Max(96, TimelineRows.Count * 18 + 18);
}

public partial class UsageStatsSliceViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isHighlighted;

    [ObservableProperty]
    private Geometry _pieGeometry = Geometry.Empty;

    public UsageStatsSliceViewModel(
        string key,
        string processName,
        TimeSpan duration,
        double share,
        SolidColorBrush swatchBrush)
    {
        Key = key;
        ProcessName = processName;
        Duration = duration;
        Share = share;
        SwatchBrush = swatchBrush;
        DurationText = MainViewModel.FormatDurationForStats(duration);
        ShareText = share.ToString("P0", System.Globalization.CultureInfo.CurrentCulture);
    }

    public string Key { get; }
    public string ProcessName { get; }
    public string Title => ProcessName;
    public TimeSpan Duration { get; }
    public double Share { get; }
    public SolidColorBrush SwatchBrush { get; }
    public System.Windows.Media.Brush Fill => SwatchBrush;
    public string DurationText { get; }
    public string PlaytimeText => DurationText;
    public string ShareText { get; }
    public string PercentageText => ShareText;

    [ObservableProperty]
    private Geometry _pieLabelLineGeometry = Geometry.Empty;

    public double PieLabelX { get; set; }
    public double PieLabelY { get; set; }
    public double PieLabelWidth { get; set; } = 22;
    public System.Windows.TextAlignment PieLabelTextAlignment { get; set; } = System.Windows.TextAlignment.Left;
    public string PieLabelText => ProcessName;
    public bool IsPieLabelVisible { get; set; }
}

public sealed class UsageTimelineRowViewModel(
    string processName,
    double top,
    IReadOnlyList<UsageTimelineSegmentViewModel> segments)
{
    public string ProcessName { get; } = processName;
    public double Top { get; } = top;
    public IReadOnlyList<UsageTimelineSegmentViewModel> Segments { get; } = segments;
}

public sealed class UsageTimelineSegmentViewModel(
    double left,
    double width,
    SolidColorBrush fill,
    string tooltip)
{
    public double Left { get; } = left;
    public double Width { get; } = width;
    public SolidColorBrush Fill { get; } = fill;
    public string Tooltip { get; } = tooltip;
}

public sealed class UsageTimelineAxisLabelViewModel(double left, string text)
{
    public double Left { get; } = left;
    public string Text { get; } = text;
}

public sealed class DesktopInsightCardViewModel(
    string title,
    string subtitle,
    string summary,
    IReadOnlyList<string> evidence,
    IReadOnlyList<string> planSuggestions,
    IReadOnlyList<string> fishbone,
    IReadOnlyList<string> constellationExplanations,
    string nextAction)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Summary { get; } = summary;
    public IReadOnlyList<string> Evidence { get; } = evidence;
    public IReadOnlyList<string> PlanSuggestions { get; } = planSuggestions;
    public IReadOnlyList<string> Fishbone { get; } = fishbone;
    public IReadOnlyList<string> ConstellationExplanations { get; } = constellationExplanations;
    public string NextAction { get; } = nextAction;
    public bool HasEvidence => Evidence.Count > 0;
    public bool HasPlanSuggestions => PlanSuggestions.Count > 0;
    public bool HasFishbone => Fishbone.Count > 0;
    public bool HasConstellationExplanations => ConstellationExplanations.Count > 0;
    public bool HasNextAction => !string.IsNullOrWhiteSpace(NextAction);
}

public sealed class CodingReviewCardViewModel(
    string title,
    string subtitle,
    string clientText,
    string workspaceText,
    string changedPathText,
    string riskText,
    IReadOnlyList<string> checkpoints,
    string verificationText)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string ClientText { get; } = clientText;
    public string WorkspaceText { get; } = workspaceText;
    public string ChangedPathText { get; } = changedPathText;
    public string RiskText { get; } = riskText;
    public IReadOnlyList<string> Checkpoints { get; } = checkpoints;
    public string VerificationText { get; } = verificationText;
    public bool HasCheckpoints => Checkpoints.Count > 0;
}

public sealed class UsageStatsCollection : ObservableCollection<UsageStatsSliceViewModel>
{
    public UsageStatsCollection()
    {
    }

    public UsageStatsCollection(IEnumerable<UsageStatsSliceViewModel> items)
        : base(items)
    {
    }
}
