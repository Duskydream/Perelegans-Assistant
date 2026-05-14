namespace Perelegans.Models;

public sealed class TaskAdventureDraft
{
    public string QuestTitle { get; set; } = string.Empty;

    public string QuestNarrative { get; set; } = string.Empty;

    public string RewardName { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string NextAction { get; set; } = string.Empty;

    public int Difficulty { get; set; } = 2;

    public int EstimatedMinutes { get; set; } = 25;

    public List<string> Tags { get; set; } = [];

    public string ConstellationName { get; set; } = string.Empty;
}
