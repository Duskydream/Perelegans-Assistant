namespace Perelegans.Models;

public enum ContextMemoryType
{
    Preference = 0,
    Project = 1,
    Decision = 2,
    Workflow = 3,
    Application = 4,
    Note = 5
}

public class ContextMemory
{
    public int Id { get; set; }

    public ContextMemoryType Type { get; set; } = ContextMemoryType.Note;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public double Weight { get; set; } = 0.6;

    public string ConstellationName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public DateTime? LastUsedAt { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double NodeSize { get; set; } = 18;

    public string TypeText => Type switch
    {
        ContextMemoryType.Preference => "Preference",
        ContextMemoryType.Project => "Project",
        ContextMemoryType.Decision => "Decision",
        ContextMemoryType.Workflow => "Workflow",
        ContextMemoryType.Application => "Application",
        _ => "Note"
    };

    public string Preview => Content.Length <= 96 ? Content : Content[..96] + "...";

    public string InsightMetaText => $"{TypeText} / {Math.Round(Math.Clamp(Weight, 0.1, 1.0), 2):0.00}";
}
