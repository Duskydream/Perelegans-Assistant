using System.Text.Json.Serialization;

namespace Perelegans.Models;

public sealed class FocusAssessmentResult
{
    [JsonPropertyName("isProductive")]
    public bool IsProductive { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}

public sealed class ScreenContextAssessmentResult
{
    [JsonPropertyName("isDeepWork")]
    public bool IsDeepWork { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}
