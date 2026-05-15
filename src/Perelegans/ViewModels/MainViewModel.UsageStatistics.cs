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
    private const byte UsageStatsFillAlpha = 150;
    private const double UsagePieCenter = 50;
    private const double UsagePieRadius = 47;

    private static readonly MediaColor[] UsageStatsPalette =
    [
        MediaColor.FromRgb(0xF0, 0x91, 0x99),
        MediaColor.FromRgb(0xF2, 0xA0, 0xA1),
        MediaColor.FromRgb(0xEE, 0x82, 0x7C),
        MediaColor.FromRgb(0xF5, 0xB1, 0xAA),
        MediaColor.FromRgb(0xEE, 0xBB, 0xCB),
        MediaColor.FromRgb(0xBC, 0x64, 0xA4),
        MediaColor.FromRgb(0xFD, 0xEF, 0xF2),
        MediaColor.FromRgb(0xE4, 0xD2, 0xD8),
        MediaColor.FromRgb(0xE3, 0x7F, 0x7F),
        MediaColor.FromRgb(0xFF, 0xD2, 0xDB)
    ];

    private string? _hoveredUsagePieKey;

    [ObservableProperty]
    private bool _isStatisticsVisible;

    [ObservableProperty]
    private string _usageStatsPeriod = "day";

    [ObservableProperty]
    private ObservableCollection<UsageStatsSliceViewModel> _usageStatsSlices = new();

    [ObservableProperty]
    private UsageStatsSliceViewModel? _highlightedUsageStatsSlice;

    [ObservableProperty]
    private string _usageStatsTitle = string.Empty;

    [ObservableProperty]
    private string _usageStatsSubtitle = string.Empty;

    [ObservableProperty]
    private string _usageStatsTotalText = string.Empty;

    public bool IsDailyUsageStatsMode => UsageStatsPeriod == "day";
    public bool IsMonthlyUsageStatsMode => UsageStatsPeriod == "month";
    public bool HasUsageStatsSlices => UsageStatsSlices.Count > 0;
    public string UsageStatsEmptyText => T("Main_UsageStatsEmpty");

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
        UsageStatsSlices = new ObservableCollection<UsageStatsSliceViewModel>(snapshot.Slices);
        UpdateUsageLegendHighlight();
        OnPropertyChanged(nameof(HasUsageStatsSlices));
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
            return new UsageStatsSnapshot(title, subtitle, T("Main_UsageStatsNoTime"), []);
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
            slices);
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
            slice.PieGeometry = CreateUsagePieSliceGeometry(startAngle, sweep);
            startAngle += sweep;
        }
    }

    private static Geometry CreateUsagePieSliceGeometry(double startAngle, double sweepAngle)
    {
        if (sweepAngle >= 359.9)
        {
            var ellipse = new EllipseGeometry(
                new WpfPoint(UsagePieCenter, UsagePieCenter),
                UsagePieRadius,
                UsagePieRadius);
            ellipse.Freeze();
            return ellipse;
        }

        var start = GetUsagePiePoint(startAngle);
        var end = GetUsagePiePoint(startAngle + sweepAngle);
        var figure = new PathFigure
        {
            StartPoint = new WpfPoint(UsagePieCenter, UsagePieCenter),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment(start, true));
        figure.Segments.Add(new ArcSegment(
            end,
            new WpfSize(UsagePieRadius, UsagePieRadius),
            0,
            sweepAngle > 180,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(new WpfPoint(UsagePieCenter, UsagePieCenter), true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static WpfPoint GetUsagePiePoint(double angle)
    {
        var radians = angle * Math.PI / 180d;
        return new WpfPoint(
            UsagePieCenter + Math.Cos(radians) * UsagePieRadius,
            UsagePieCenter + Math.Sin(radians) * UsagePieRadius);
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
}
