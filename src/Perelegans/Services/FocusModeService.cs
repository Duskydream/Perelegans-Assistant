using Perelegans.Models;

namespace Perelegans.Services;

public sealed class FocusModeService
{
    public bool IsActive { get; private set; }

    public int? TaskMemoryId { get; private set; }

    public string TaskTitle { get; private set; } = string.Empty;

    public string TaskTags { get; private set; } = string.Empty;

    public string NextAction { get; private set; } = string.Empty;

    public DateTime? StartedAt { get; private set; }

    public event Action? StateChanged;

    public void Start(ContextMemory memory)
    {
        IsActive = true;
        TaskMemoryId = memory.Id;
        TaskTitle = memory.Title;
        TaskTags = memory.Tags;
        NextAction = memory.NextPrediction;
        StartedAt = DateTime.Now;
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        TaskMemoryId = null;
        TaskTitle = string.Empty;
        TaskTags = string.Empty;
        NextAction = string.Empty;
        StartedAt = null;
        StateChanged?.Invoke();
    }
}
