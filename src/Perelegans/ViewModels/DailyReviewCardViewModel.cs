namespace Perelegans.ViewModels;

public sealed class DailyReviewCardViewModel(
    string title,
    string subtitle,
    string encouragementTitle,
    string encouragement,
    string overviewTitle,
    string overview,
    string highlightsTitle,
    IReadOnlyList<string> highlights,
    string risksTitle,
    IReadOnlyList<string> risks,
    string nextActionTitle,
    string nextAction,
    IReadOnlyList<DailyReviewMetricViewModel> metrics,
    string savedContextText)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string EncouragementTitle { get; } = encouragementTitle;
    public string Encouragement { get; } = encouragement;
    public string OverviewTitle { get; } = overviewTitle;
    public string Overview { get; } = overview;
    public string HighlightsTitle { get; } = highlightsTitle;
    public IReadOnlyList<string> Highlights { get; } = highlights;
    public string RisksTitle { get; } = risksTitle;
    public IReadOnlyList<string> Risks { get; } = risks;
    public string NextActionTitle { get; } = nextActionTitle;
    public string NextAction { get; } = nextAction;
    public IReadOnlyList<DailyReviewMetricViewModel> Metrics { get; } = metrics;
    public string SavedContextText { get; } = savedContextText;
    public bool HasHighlights => Highlights.Count > 0;
    public bool HasRisks => Risks.Count > 0;
    public bool HasNextAction => !string.IsNullOrWhiteSpace(NextAction);
    public bool HasSavedContext => !string.IsNullOrWhiteSpace(SavedContextText);
}

public sealed class DailyReviewMetricViewModel(
    string label,
    string value,
    string caption)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public string Caption { get; } = caption;
}
