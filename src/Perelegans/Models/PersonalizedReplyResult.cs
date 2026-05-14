using System.Text.Json.Serialization;

namespace Perelegans.Models;

public sealed class PersonalizedReplyResult
{
    [JsonPropertyName("reply")]
    public string Reply { get; set; } = string.Empty;

    [JsonPropertyName("usedMemoryIds")]
    public List<int> UsedMemoryIds { get; set; } = [];

    [JsonPropertyName("suggestedMemory")]
    public MemoryCandidateResult? SuggestedMemory { get; set; }
}
