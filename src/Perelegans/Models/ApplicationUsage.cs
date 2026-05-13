using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Perelegans.Models;

public enum ApplicationFocusCategory
{
    Unknown = 0,
    Productive = 1,
    Distracting = 2,
    Neutral = 3
}

public class ApplicationUsage : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _processName = string.Empty;
    private string _executablePath = string.Empty;
    private TimeSpan _totalDuration = TimeSpan.Zero;
    private DateTime _firstSeenAt = DateTime.Now;
    private DateTime _lastSeenAt = DateTime.Now;
    private ApplicationFocusCategory _category = ApplicationFocusCategory.Unknown;
    private string? _aiDescription;
    private string? _lastAssistantMessage;

    public int Id { get; set; }

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public string ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetField(ref _executablePath, value);
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set => SetField(ref _totalDuration, value);
    }

    public DateTime FirstSeenAt
    {
        get => _firstSeenAt;
        set => SetField(ref _firstSeenAt, value);
    }

    public DateTime LastSeenAt
    {
        get => _lastSeenAt;
        set => SetField(ref _lastSeenAt, value);
    }

    public ApplicationFocusCategory Category
    {
        get => _category;
        set => SetField(ref _category, value);
    }

    public string? AiDescription
    {
        get => _aiDescription;
        set => SetField(ref _aiDescription, value);
    }

    public string? LastAssistantMessage
    {
        get => _lastAssistantMessage;
        set => SetField(ref _lastAssistantMessage, value);
    }

    public List<ApplicationUsageSession> Sessions { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
