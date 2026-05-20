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
    private const byte UsageStatsLightFillAlpha = 188;
    private const byte UsageStatsDarkFillAlpha = 172;
    private const double UsagePieCenter = 50;
    private const double UsageDonutInnerRadius = 27;
    private const double UsageDonutOuterRadius = 42;
    private const double UsagePieExplodeOffset = 2.2;

    private static readonly MediaColor[] UsageStatsLightPalette =
    [
        MediaColor.FromRgb(0xEC, 0x82, 0xAD),
        MediaColor.FromRgb(0x79, 0xB5, 0xE0),
        MediaColor.FromRgb(0x9C, 0xC9, 0x78),
        MediaColor.FromRgb(0xE0, 0xB6, 0x5E),
        MediaColor.FromRgb(0xB0, 0x8D, 0xD8),
        MediaColor.FromRgb(0x66, 0xC8, 0xBA),
        MediaColor.FromRgb(0xDC, 0x8A, 0x70),
        MediaColor.FromRgb(0xC0, 0xA0, 0x8F),
        MediaColor.FromRgb(0x9B, 0xAF, 0xC8),
        MediaColor.FromRgb(0xD0, 0x8F, 0xB0)
    ];

    private static readonly MediaColor[] UsageStatsDarkPalette =
    [
        MediaColor.FromRgb(0x54, 0xE8, 0xFF),
        MediaColor.FromRgb(0xFF, 0x6F, 0xB4),
        MediaColor.FromRgb(0x8F, 0x7B, 0xFF),
        MediaColor.FromRgb(0x62, 0xF2, 0xB4),
        MediaColor.FromRgb(0xFF, 0xD1, 0x66),
        MediaColor.FromRgb(0x4D, 0xA3, 0xFF),
        MediaColor.FromRgb(0xFF, 0x8A, 0x6A),
        MediaColor.FromRgb(0xA8, 0xF0, 0x72),
        MediaColor.FromRgb(0xD8, 0x7C, 0xFF),
        MediaColor.FromRgb(0x65, 0xE5, 0xDA)
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
    public string UsagePieCenterTitle => HighlightedUsageStatsSlice?.Title ?? "Total";
    public string UsagePieCenterValue => HighlightedUsageStatsSlice?.DurationText ?? UsageStatsTotalText;
    public string UsagePieCenterCaption => HighlightedUsageStatsSlice?.PercentageText ?? "tracked time";
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
            IsCompanionRoomVisible = false;
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
        ApplyUsageStatsSnapshot(CreateUsageStatsSnapshot(sessions, title, subtitle, start, now, IsUsageStatsDarkThemeActive()));
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
        OnPropertyChanged(nameof(UsagePieCenterTitle));
        OnPropertyChanged(nameof(UsagePieCenterValue));
        OnPropertyChanged(nameof(UsagePieCenterCaption));
    }

    private static UsageStatsSnapshot CreateDailyReviewUsageStatsSnapshot(IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var start = DateTime.Now.AddHours(-24);
        return CreateUsageStatsSnapshot(
            sessions,
            T("Main_UsageStatsReviewTitle"),
            T("Main_UsageStatsReviewSubtitle"),
            start,
            DateTime.Now,
            IsUsageStatsDarkThemeActive());
    }

    private static UsageStatsSnapshot CreateUsageStatsSnapshot(
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        string title,
        string subtitle,
        DateTime start,
        DateTime end,
        bool useDarkPalette)
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
        var palette = useDarkPalette ? UsageStatsDarkPalette : UsageStatsLightPalette;
        var fillAlpha = useDarkPalette ? UsageStatsDarkFillAlpha : UsageStatsLightFillAlpha;
        for (var i = 0; i < chartItems.Count; i++)
        {
            var item = chartItems[i];
            var share = item.Duration.TotalSeconds / total.TotalSeconds;
            var color = palette[i % palette.Length];
            slices.Add(new UsageStatsSliceViewModel(
                item.ProcessName,
                item.ProcessName,
                item.Duration,
                share,
                CreateBrush(color, fillAlpha)));
        }

        AssignUsagePieGeometries(slices);

        return new UsageStatsSnapshot(
            title,
            subtitle,
            string.Format(T("Main_UsageStatsTotalFormat"), FormatDurationForStats(total)),
            CreateUsageBehaviorInsight(sessions, grouped.Select(item => (item.ProcessName, item.Duration)).ToList(), total, start, end),
            slices,
            CreateUsageTimelineRows(sessions, chartItems.Select(item => item.ProcessName).ToList(), slices, start, end, useDarkPalette),
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
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var sweep = i == slices.Count - 1
                ? 270d - startAngle
                : Math.Max(0d, slice.Share * 360d);
            var explodeOffset = slices.Count > 1 ? UsagePieExplodeOffset : 0;
            slice.PieGeometry = CreateUsageDonutSliceGeometry(startAngle, sweep, explodeOffset);
            slice.PieLabelLineGeometry = Geometry.Empty;
            slice.IsPieLabelVisible = false;
            startAngle += sweep;
        }
    }

    private static Geometry CreateUsageDonutSliceGeometry(double startAngle, double sweepAngle, double explodeOffset)
    {
        var midAngle = startAngle + sweepAngle / 2d;
        var center = GetUsagePieShiftedCenter(midAngle, explodeOffset);
        if (sweepAngle >= 359.9)
        {
            var ringGeometry = new GeometryGroup
            {
                FillRule = FillRule.EvenOdd
            };
            ringGeometry.Children.Add(new EllipseGeometry(center, UsageDonutOuterRadius, UsageDonutOuterRadius));
            ringGeometry.Children.Add(new EllipseGeometry(center, UsageDonutInnerRadius, UsageDonutInnerRadius));
            ringGeometry.Freeze();
            return ringGeometry;
        }

        var outerStart = GetUsagePiePoint(center, startAngle, UsageDonutOuterRadius);
        var outerEnd = GetUsagePiePoint(center, startAngle + sweepAngle, UsageDonutOuterRadius);
        var innerStart = GetUsagePiePoint(center, startAngle, UsageDonutInnerRadius);
        var innerEnd = GetUsagePiePoint(center, startAngle + sweepAngle, UsageDonutInnerRadius);
        var figure = new PathFigure
        {
            StartPoint = outerStart,
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new ArcSegment(
            outerEnd,
            new WpfSize(UsageDonutOuterRadius, UsageDonutOuterRadius),
            0,
            sweepAngle > 180,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(innerEnd, true));
        figure.Segments.Add(new ArcSegment(
            innerStart,
            new WpfSize(UsageDonutInnerRadius, UsageDonutInnerRadius),
            0,
            sweepAngle > 180,
            SweepDirection.Counterclockwise,
            true));

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
        DateTime end,
        bool useDarkPalette)
    {
        var durationSeconds = Math.Max(1, (end - start).TotalSeconds);
        var brushByProcess = slices.ToDictionary(slice => slice.ProcessName, slice => slice.SwatchBrush);
        var rows = new List<UsageTimelineRowViewModel>();
        var fallbackPalette = useDarkPalette ? UsageStatsDarkPalette : UsageStatsLightPalette;
        var fallbackFillAlpha = useDarkPalette ? UsageStatsDarkFillAlpha : UsageStatsLightFillAlpha;
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
                        brushByProcess.GetValueOrDefault(processName) ?? CreateBrush(fallbackPalette[rows.Count % fallbackPalette.Length], fallbackFillAlpha),
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
        OnPropertyChanged(nameof(UsagePieCenterTitle));
        OnPropertyChanged(nameof(UsagePieCenterValue));
        OnPropertyChanged(nameof(UsagePieCenterCaption));
    }

    private static TimeSpan ClipDuration(ApplicationUsageSession session, DateTime start, DateTime end)
    {
        var from = session.StartTime > start ? session.StartTime : start;
        var to = session.EndTime < end ? session.EndTime : end;
        return to > from ? to - from : TimeSpan.Zero;
    }

    private static SolidColorBrush CreateBrush(MediaColor color, byte alpha)
    {
        var brush = new SolidColorBrush(MediaColor.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static bool IsUsageStatsDarkThemeActive() => IsCurrentThemeResourceDark();

    private static bool IsCurrentThemeResourceDark()
    {
        if (System.Windows.Application.Current?.Resources["Perelegans.WindowBackground"] is not SolidColorBrush brush)
        {
            return false;
        }

        var color = brush.Color;
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
        return luminance < 0.35;
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
