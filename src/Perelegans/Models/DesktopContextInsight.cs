using System.Text.Json.Serialization;

namespace Perelegans.Models;

public sealed class DesktopContextInsight
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("planSuggestions")]
    public List<string> PlanSuggestions { get; set; } = [];

    [JsonPropertyName("fishbone")]
    public List<string> Fishbone { get; set; } = [];

    [JsonPropertyName("constellationExplanations")]
    public List<string> ConstellationExplanations { get; set; } = [];

    [JsonPropertyName("suggestedNextAction")]
    public string SuggestedNextAction { get; set; } = string.Empty;
}
