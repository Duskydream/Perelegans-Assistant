namespace Perelegans.Models;

public class FocusTaskLink
{
    public int Id { get; set; }

    public int SourceTaskId { get; set; }

    public int TargetTaskId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public double Strength { get; set; } = 0.5;
}
