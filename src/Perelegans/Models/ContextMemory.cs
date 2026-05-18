namespace Perelegans.Models;

public enum ContextMemoryType
{
    Preference = 0,
    Project = 1,
    Decision = 2,
    Workflow = 3,
    Application = 4,
    Note = 5,
    Event = 6,
    Task = 7,
    Review = 8
}

public enum ContextMemoryLifecycle
{
    Active = 0,
    Stale = 1,
    Archived = 2,
    Contradicted = 3
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

    public int MentionCount { get; set; }

    public string MemoryAxis { get; set; } = "event";

    public string AiDescription { get; set; } = string.Empty;

    public string AiExplanation { get; set; } = string.Empty;

    public string NextPrediction { get; set; } = string.Empty;

    public bool IsPlan { get; set; }

    public bool IsCompleted { get; set; }

    public DateTime? CompletedAt { get; set; }

    public bool IsAbandoned { get; set; }

    public DateTime? AbandonedAt { get; set; }

    public ContextMemoryLifecycle Lifecycle { get; set; } = ContextMemoryLifecycle.Active;

    public string AiWeightProfile { get; set; } = string.Empty;

    public string ConstellationName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public DateTime? LastUsedAt { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double NodeSize { get; set; } = 18;

    public bool IsLowWeight => Weight < 0.35 || Lifecycle == ContextMemoryLifecycle.Stale;

    public double VisualOpacity => IsLowWeight ? 0.45 : 1.0;

    public string TypeText => Type switch
    {
        ContextMemoryType.Preference => "偏好",
        ContextMemoryType.Project => "项目",
        ContextMemoryType.Decision => "决定",
        ContextMemoryType.Workflow => "流程",
        ContextMemoryType.Application => "应用",
        ContextMemoryType.Event => "事件",
        ContextMemoryType.Task => "任务",
        ContextMemoryType.Review => "复盘",
        _ => "笔记"
    };

    public string Preview => Content.Length <= 96 ? Content : Content[..96] + "...";

    public string PlanStatusText => IsPlan
        ? IsAbandoned ? "plan: abandoned" : IsCompleted ? "plan: done" : "plan: open"
        : MemoryAxis;

    public string InsightMetaText => $"{TypeText} / {PlanStatusText} / {Lifecycle} / {Math.Round(Math.Clamp(Weight, 0.1, 1.0), 2):0.00} / used {MentionCount}";
}
