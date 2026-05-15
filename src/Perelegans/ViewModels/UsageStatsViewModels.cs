using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Perelegans.ViewModels;

public sealed class UsageStatsSnapshot(
    string title,
    string subtitle,
    string totalText,
    IReadOnlyList<UsageStatsSliceViewModel> slices)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string TotalText { get; } = totalText;
    public IReadOnlyList<UsageStatsSliceViewModel> Slices { get; } = slices;
    public bool HasSlices => Slices.Count > 0;
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
