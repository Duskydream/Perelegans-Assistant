namespace Perelegans.Models;

public class BreakpointSnapshot
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LeftAt { get; set; } = DateTime.Now;
    public DateTime? ReturnedAt { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string RelatedPlanTitle { get; set; } = string.Empty;
    public int? RelatedMemoryId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string NextStep { get; set; } = string.Empty;
    public bool WasShown { get; set; }
    public bool IsDismissed { get; set; }
}
