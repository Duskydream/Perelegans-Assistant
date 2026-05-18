using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Perelegans.ViewModels;

public partial class MainViewModel
{
    private const byte UsageStatsFillAlpha = 238;
    private const double UsagePieCenter = 50;
    private const double UsagePieMinRadius = 28;
    private const double UsagePieMaxRadius = 42;
    private const double UsagePieExplodeOffset = 4.5;

    private static readonly MediaColor[] UsageStatsPalette =
    [
        MediaColor.FromRgb(0x67, 0x7F, 0xE4),
        MediaColor.FromRgb(0x8F, 0xCF, 0x73),
        MediaColor.FromRgb(0xFF, 0xCA, 0x57),
        MediaColor.FromRgb(0xF4, 0x63, 0x67),
        MediaColor.FromRgb(0x6E, 0xC4, 0xE2),
        MediaColor.FromRgb(0xA2, 0x73, 0xD4),
        MediaColor.FromRgb(0xF6, 0x98, 0x4F),
        MediaColor.FromRgb(0x55, 0xCC, 0xB2),
        MediaColor.FromRgb(0xE8, 0x64, 0xAE),
        MediaColor.FromRgb(0xB7, 0xCA, 0x4E)
    ];

    private string? _hoveredUsagePieKey;

    [ObservableProperty]
    private bool _isStatisticsVisible;

    [ObservableProperty]
    private string _usageStatsPeriod = "day";

    [ObservableProperty]
    private ObservableCollection<UsageStatsSliceViewModel> _usageStatsSlices = new();

    [ObservableProperty]
    private ObservableCollection<UsageTimelineRowViewModel> _usageTimelineRows = new();

    [ObservableProperty]
    private ObservableCollection<UsageTimelineAxisLabelViewModel> _usageTimelineAxisLabels = new();

    [ObservableProperty]
    private UsageStatsSliceViewModel? _highlightedUsageStatsSlice;

    [ObservableProperty]
    private string _usageStatsTitle = string.Empty;

    [ObservableProperty]
    private string _usageStatsSubtitle = string.Empty;

    [ObservableProperty]
    private string _usageStatsTotalText = string.Empty;

    [ObservableProperty]
    private string _usageStatsInsightText = string.Empty;

    public bool IsDailyUsageStatsMode => UsageStatsPeriod == "day";
    public bool IsMonthlyUsageStatsMode => UsageStatsPeriod == "month";
    public bool HasUsageStatsSlices => UsageStatsSlices.Count > 0;
    public bool HasUsageTimelineRows => UsageTimelineRows.Count > 0;
    public double UsageTimelineChartHeight => Math.Max(96, UsageTimelineRows.Count * 18 + 18);
    public string UsageStatsEmptyText => T("Main_UsageStatsEmpty");
    public string UsageStatsTimelineTitle => T("Main_UsageStatsTimelineTitle");
    public string UsageTimelineStartLabel => UsageTimelineAxisLabels.ElementAtOrDefault(0)?.Text ?? string.Empty;
    public string UsageTimelineMiddleLabel => UsageTimelineAxisLabels.ElementAtOrDefault(1)?.Text ?? string.Empty;
    public string UsageTimelineEndLabel => UsageTimelineAxisLabels.ElementAtOrDefault(2)?.Text ?? string.Empty;

    [RelayCommand]
    private async Task ToggleStatistics()
    {
        IsStatisticsVisible = !IsStatisticsVisible;
        if (IsStatisticsVisible)
        {
            IsGalaxyVisible = false;
            await RefreshUsageStatisticsAsync();
        }
    }

    [RelayCommand]
    private async Task SetUsageStatsPeriod(string period)
    {
        UsageStatsPeriod = period == "month" ? "month" : "day";
        await RefreshUsageStatisticsAsync();
    }

    public void ClearUsagePieHover()
    {
        SetHoveredUsagePieKey(null);
    }

    public void SetHoveredUsageSlice(UsageStatsSliceViewModel? slice)
    {
        SetHoveredUsagePieKey(slice?.Key);
    }

    private async Task RefreshUsageStatisticsAsync()
    {
        var now = DateTime.Now;
        var start = UsageStatsPeriod == "month"
            ? new DateTime(now.Year, now.Month, 1)
            : DateTime.Today;
        var title = UsageStatsPeriod == "month"
            ? T("Main_UsageStatsMonthlyTitle")
            : T("Main_UsageStatsDailyTitle");
        var subtitle = UsageStatsPeriod == "month"
            ? string.Format(T("Main_UsageStatsMonthlySubtitleFormat"), start.ToString("yyyy-MM"))
            : string.Format(T("Main_UsageStatsDailySubtitleFormat"), start.ToString("MM-dd"));

        var sessions = await _dbService.GetApplicationUsageSessionsSinceAsync(start);
        ApplyUsageStatsSnapshot(CreateUsageStatsSnapshot(sessions, title, subtitle, start, now));
    }

    private void ApplyUsageStatsSnapshot(UsageStatsSnapshot snapshot)
    {
        UsageStatsTitle = snapshot.Title;
        UsageStatsSubtitle = snapshot.Subtitle;
        UsageStatsTotalText = snapshot.TotalText;
        UsageStatsInsightText = snapshot.InsightText;
        UsageStatsSlices = new ObservableCollection<UsageStatsSliceViewModel>(snapshot.Slices);
        UsageTimelineRows = new ObservableCollection<UsageTimelineRowViewModel>(snapshot.TimelineRows);
        UsageTimelineAxisLabels = new ObservableCollection<UsageTimelineAxisLabelViewModel>(snapshot.TimelineAxisLabels);
        UpdateUsageLegendHighlight();
        OnPropertyChanged(nameof(HasUsageStatsSlices));
        OnPropertyChanged(nameof(HasUsageTimelineRows));
        OnPropertyChanged(nameof(UsageTimelineChartHeight));
        OnPropertyChanged(nameof(UsageTimelineStartLabel));
        OnPropertyChanged(nameof(UsageTimelineMiddleLabel));
        OnPropertyChanged(nameof(UsageTimelineEndLabel));
    }

    private static UsageStatsSnapshot CreateDailyReviewUsageStatsSnapshot(IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var start = DateTime.Now.AddHours(-24);
        return CreateUsageStatsSnapshot(
            sessions,
            T("Main_UsageStatsReviewTitle"),
            T("Main_UsageStatsReviewSubtitle"),
            start,
            DateTime.Now);
    }

    private static UsageStatsSnapshot CreateUsageStatsSnapshot(
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        string title,
        string subtitle,
        DateTime start,
        DateTime end)
    {
        var grouped = sessions
            .Select(session => new
            {
                session.ProcessName,
                Duration = ClipDuration(session, start, end)
            })
            .Where(item => item.Duration.TotalSeconds >= 1)
            .GroupBy(item => item.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Duration = group.Aggregate(TimeSpan.Zero, (total, item) => total + item.Duration)
            })
            .OrderByDescending(item => item.Duration)
            .ToList();

        var total = grouped.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Duration);
        if (total.TotalSeconds < 1)
        {
            return new UsageStatsSnapshot(title, subtitle, T("Main_UsageStatsNoTime"), "还没有足够的行为信号，我先安静收集一会儿。", []);
        }

        var top = grouped.Take(7).ToList();
        var other = grouped.Skip(7).Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Duration);
        var chartItems = top
            .Select(item => (item.ProcessName, item.Duration))
            .ToList();
        if (other.TotalSeconds >= 1)
        {
            chartItems.Add((T("Main_UsageStatsOther"), other));
        }

        var slices = new List<UsageStatsSliceViewModel>();
        for (var i = 0; i < chartItems.Count; i++)
        {
            var item = chartItems[i];
            var share = item.Duration.TotalSeconds / total.TotalSeconds;
            var color = UsageStatsPalette[i % UsageStatsPalette.Length];
            slices.Add(new UsageStatsSliceViewModel(
                item.ProcessName,
                item.ProcessName,
                item.Duration,
                share,
                CreateBrush(color)));
        }

        AssignUsagePieGeometries(slices);

        return new UsageStatsSnapshot(
            title,
            subtitle,
            string.Format(T("Main_UsageStatsTotalFormat"), FormatDurationForStats(total)),
            CreateUsageBehaviorInsight(sessions, grouped.Select(item => (item.ProcessName, item.Duration)).ToList(), total, start, end),
            slices,
            CreateUsageTimelineRows(sessions, chartItems.Select(item => item.ProcessName).ToList(), slices, start, end),
            CreateUsageTimelineAxisLabels(start, end));
    }

    private static string CreateUsageBehaviorInsight(
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        IReadOnlyList<(string ProcessName, TimeSpan Duration)> grouped,
        TimeSpan total,
        DateTime start,
        DateTime end)
    {
        var clippedSessions = sessions
            .Select(session => new
            {
                session.ProcessName,
                StartTime = session.StartTime < start ? start : session.StartTime,
                EndTime = session.EndTime > end ? end : session.EndTime,
                Duration = ClipDuration(session, start, end)
            })
            .Where(item => item.Duration.TotalSeconds >= 30)
            .OrderBy(item => item.StartTime)
            .ToList();
        var top = grouped.FirstOrDefault();
        if (top.ProcessName == null)
        {
            return "这段时间的窗口记录太少，暂时不做行为判断。";
        }

        var longest = clippedSessions
            .OrderByDescending(item => item.Duration)
            .FirstOrDefault();
        var switchesPerHour = total.TotalHours <= 0
            ? 0
            : Math.Max(0, clippedSessions.Count - 1) / total.TotalHours;
        var share = top.Duration.TotalSeconds / Math.Max(1, total.TotalSeconds);
        var flowLine = longest == null
            ? "连续工作段还不明显"
            : $"最长连续段是 {longest.ProcessName}，约 {FormatDurationForStats(longest.Duration)}";

        if (share >= 0.58 && longest?.Duration >= TimeSpan.FromMinutes(25))
        {
            return $"今天主工作流很集中在 {top.ProcessName}，{flowLine}，看起来更像一段稳定推进。";
        }

        if (switchesPerHour >= 10)
        {
            return $"这段时间切换偏密，主线是 {top.ProcessName}，但可能夹杂查资料或分心；可以回看时间线里最碎的几段。";
        }

        if (grouped.Count >= 3 && share < 0.4)
        {
            return $"行为分布比较分散，没有单一应用压倒性占据时间；如果这是查资料，它是正常切换，如果不是，就适合收束到一个下一步。";
        }

        return $"主要时间落在 {top.ProcessName}，{flowLine}；整体切换节奏还算平稳。";
    }

    private static void AssignUsagePieGeometries(IReadOnlyList<UsageStatsSliceViewModel> slices)
    {
        var startAngle = -90d;
        var maxShare = Math.Max(0.001d, slices.Max(slice => slice.Share));
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var sweep = i == slices.Count - 1
                ? 270d - startAngle
                : Math.Max(0d, slice.Share * 360d);
            var midAngle = startAngle + sweep / 2d;
            var radius = UsagePieMinRadius + Math.Sqrt(slice.Share / maxShare) * (UsagePieMaxRadius - UsagePieMinRadius);
            var explodeOffset = slices.Count > 1 ? UsagePieExplodeOffset : 0;
            slice.PieGeometry = CreateUsagePieSliceGeometry(startAngle, sweep, explodeOffset, radius);
            slice.PieLabelLineGeometry = Geometry.Empty;
            slice.IsPieLabelVisible = false;
            startAngle += sweep;
        }
    }

    private static Geometry CreateUsagePieSliceGeometry(double startAngle, double sweepAngle, double explodeOffset, double radius)
    {
        var midAngle = startAngle + sweepAngle / 2d;
        var center = GetUsagePieShiftedCenter(midAngle, explodeOffset);
        if (sweepAngle >= 359.9)
        {
            var ellipse = new EllipseGeometry(
                center,
                radius,
                radius);
            ellipse.Freeze();
            return ellipse;
        }

        var start = GetUsagePiePoint(center, startAngle, radius);
        var end = GetUsagePiePoint(center, startAngle + sweepAngle, radius);
        var figure = new PathFigure
        {
            StartPoint = center,
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment(start, true));
        figure.Segments.Add(new ArcSegment(
            end,
            new WpfSize(radius, radius),
            0,
            sweepAngle > 180,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(center, true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static WpfPoint GetUsagePieShiftedCenter(double angle, double explodeOffset)
    {
        var radians = angle * Math.PI / 180d;
        return new WpfPoint(
            UsagePieCenter + Math.Cos(radians) * explodeOffset,
            UsagePieCenter + Math.Sin(radians) * explodeOffset);
    }

    private static WpfPoint GetUsagePiePoint(WpfPoint center, double angle, double radius)
    {
        var radians = angle * Math.PI / 180d;
        return new WpfPoint(
            center.X + Math.Cos(radians) * radius,
            center.Y + Math.Sin(radians) * radius);
    }

    private static IReadOnlyList<UsageTimelineRowViewModel> CreateUsageTimelineRows(
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        IReadOnlyList<string> processNames,
        IReadOnlyList<UsageStatsSliceViewModel> slices,
        DateTime start,
        DateTime end)
    {
        var durationSeconds = Math.Max(1, (end - start).TotalSeconds);
        var brushByProcess = slices.ToDictionary(slice => slice.ProcessName, slice => slice.SwatchBrush);
        var rows = new List<UsageTimelineRowViewModel>();
        foreach (var processName in processNames.Take(8))
        {
            var rowTop = rows.Count * 18d;
            var segments = sessions
                .Where(session => string.Equals(session.ProcessName, processName, StringComparison.Ordinal))
                .Select(session =>
                {
                    var from = session.StartTime > start ? session.StartTime : start;
                    var to = session.EndTime < end ? session.EndTime : end;
                    if (to <= from)
                    {
                        return null;
                    }

                    var left = Math.Clamp((from - start).TotalSeconds / durationSeconds * 100d, 0d, 100d);
                    var availableWidth = 100d - left;
                    if (availableWidth <= 0d)
                    {
                        return null;
                    }

                    var rawWidth = (to - from).TotalSeconds / durationSeconds * 100d;
                    var minVisibleWidth = Math.Min(0.7d, availableWidth);
                    var width = Math.Clamp(rawWidth, minVisibleWidth, availableWidth);
                    return new UsageTimelineSegmentViewModel(
                        left,
                        width,
                        brushByProcess.GetValueOrDefault(processName) ?? CreateBrush(UsageStatsPalette[rows.Count % UsageStatsPalette.Length]),
                        $"{processName} {from:HH:mm}-{to:HH:mm}");
                })
                .Where(segment => segment != null)
                .Cast<UsageTimelineSegmentViewModel>()
                .ToList();

            if (segments.Count > 0)
            {
                rows.Add(new UsageTimelineRowViewModel(processName, rowTop, segments));
            }
        }

        return rows;
    }

    private static IReadOnlyList<UsageTimelineAxisLabelViewModel> CreateUsageTimelineAxisLabels(DateTime start, DateTime end)
    {
        var middle = start + TimeSpan.FromTicks((end - start).Ticks / 2);
        return
        [
            new UsageTimelineAxisLabelViewModel(0, start.ToString("HH:mm")),
            new UsageTimelineAxisLabelViewModel(49, middle.ToString("HH:mm")),
            new UsageTimelineAxisLabelViewModel(93, end.ToString("HH:mm"))
        ];
    }

    private void SetHoveredUsagePieKey(string? hoveredKey)
    {
        if (string.Equals(_hoveredUsagePieKey, hoveredKey, StringComparison.Ordinal))
        {
            return;
        }

        _hoveredUsagePieKey = hoveredKey;
        UpdateUsageLegendHighlight();
    }

    private void UpdateUsageLegendHighlight()
    {
        UsageStatsSliceViewModel? highlighted = null;
        foreach (var item in UsageStatsSlices)
        {
            var isHighlighted = _hoveredUsagePieKey != null &&
                string.Equals(item.Key, _hoveredUsagePieKey, StringComparison.Ordinal);
            item.IsHighlighted = isHighlighted;
            if (isHighlighted)
            {
                highlighted = item;
            }
        }

        HighlightedUsageStatsSlice = highlighted;
    }

    private static TimeSpan ClipDuration(ApplicationUsageSession session, DateTime start, DateTime end)
    {
        var from = session.StartTime > start ? session.StartTime : start;
        var to = session.EndTime < end ? session.EndTime : end;
        return to > from ? to - from : TimeSpan.Zero;
    }

    private static SolidColorBrush CreateBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(MediaColor.FromArgb(UsageStatsFillAlpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static string FormatDurationForStats(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return string.Format(T("Main_HoursMinutesFormat"), (int)duration.TotalHours, duration.Minutes);
        }

        return string.Format(T("Main_MinutesFormat"), Math.Max(1, (int)Math.Round(duration.TotalMinutes)));
    }

    partial void OnUsageStatsPeriodChanged(string value)
    {
        OnPropertyChanged(nameof(IsDailyUsageStatsMode));
        OnPropertyChanged(nameof(IsMonthlyUsageStatsMode));
    }

    partial void OnUsageStatsSlicesChanged(ObservableCollection<UsageStatsSliceViewModel> value)
    {
        OnPropertyChanged(nameof(HasUsageStatsSlices));
    }

    partial void OnUsageTimelineRowsChanged(ObservableCollection<UsageTimelineRowViewModel> value)
    {
        OnPropertyChanged(nameof(HasUsageTimelineRows));
        OnPropertyChanged(nameof(UsageTimelineChartHeight));
    }
}
