namespace Perelegans.Models;

public sealed class DailyReviewDraft
{
    public string Review { get; set; } = string.Empty;

    public List<string> Highlights { get; set; } = [];

    public List<string> Risks { get; set; } = [];

    public string SuggestedNextAction { get; set; } = string.Empty;
}
