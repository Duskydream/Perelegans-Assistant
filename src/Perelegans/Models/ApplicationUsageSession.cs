namespace Perelegans.Models;

public class ApplicationUsageSession
{
    public int Id { get; set; }
    public int ApplicationUsageId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public bool IsKnownProductivityApp { get; set; }

    public ApplicationUsage ApplicationUsage { get; set; } = null!;
}
