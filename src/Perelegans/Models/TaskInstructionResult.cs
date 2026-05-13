namespace Perelegans.Models;

public sealed class TaskInstructionResult
{
    public string Intent { get; set; } = "none";

    public string Command { get; set; } = "none";

    public string PrimaryTask { get; set; } = string.Empty;

    public List<string> Tasks { get; set; } = [];

    public string AssistantMessage { get; set; } = string.Empty;

    public double Confidence { get; set; }
}
