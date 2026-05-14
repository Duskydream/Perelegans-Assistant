using System.Text.Json.Serialization;

namespace Perelegans.Models;

public sealed class MemoryCandidateResult
{
    [JsonPropertyName("shouldRemember")]
    public bool ShouldRemember { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Note";

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 0.6;

    [JsonPropertyName("reply")]
    public string Reply { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}
