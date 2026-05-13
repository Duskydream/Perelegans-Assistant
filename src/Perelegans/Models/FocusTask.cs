namespace Perelegans.Models;

public enum FocusTaskStatus
{
    Active = 0,
    Completed = 1,
    Abandoned = 2
}

public class FocusTask
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string OriginalInput { get; set; } = string.Empty;

    public string QuestTitle { get; set; } = string.Empty;

    public string QuestNarrative { get; set; } = string.Empty;

    public string CompletionNarrative { get; set; } = string.Empty;

    public string RewardName { get; set; } = string.Empty;

    public FocusTaskStatus Status { get; set; } = FocusTaskStatus.Active;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? CompletedAt { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double NodeSize { get; set; } = 18;
}
