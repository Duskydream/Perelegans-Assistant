using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class FloatingPetViewModel : ObservableObject, IDisposable
{
    private readonly ProcessMonitorService _processMonitor;
    private readonly FocusClassificationClient _focusClassificationClient;
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly FocusModeService _focusModeService;
    private readonly Action _showDashboard;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _bubbleText = "Perelegans 正在待命";

    [ObservableProperty]
    private string _currentProcessName = string.Empty;

    [ObservableProperty]
    private TimeSpan _currentDuration;

    [ObservableProperty]
    private bool _isProductive;

    public FloatingPetViewModel(
        ProcessMonitorService processMonitor,
        FocusClassificationClient focusClassificationClient,
        DatabaseService databaseService,
        SettingsService settingsService,
        FocusModeService focusModeService,
        Action showDashboard,
        Action openSettings,
        Action exitApplication)
    {
        _processMonitor = processMonitor;
        _focusClassificationClient = focusClassificationClient;
        _databaseService = databaseService;
        _settingsService = settingsService;
        _focusModeService = focusModeService;
        _showDashboard = showDashboard;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        _focusModeService.StateChanged += OnFocusModeStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        _ = SampleNowAsync();
    }

    public bool IsMonitorEnabled => _settingsService.Settings.MonitorEnabled;

    public string MonitorMenuText => IsMonitorEnabled ? "先歇一会" : "开始陪伴";

    public bool IsFocusModeActive => _focusModeService.IsActive;

    public string FocusModeMenuText => IsFocusModeActive ? "结束专注" : "专注模式";

    [RelayCommand]
    private void ShowDashboard()
    {
        _showDashboard();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _openSettings();
    }

    [RelayCommand]
    private void ToggleMonitor()
    {
        SetMonitorEnabled(!_settingsService.Settings.MonitorEnabled);
    }

    [RelayCommand]
    private async Task ToggleFocusMode()
    {
        if (_focusModeService.IsActive)
        {
            _focusModeService.Stop();
            BubbleText = "专注模式已结束。";
            return;
        }

        var memory = await _databaseService.GetLatestOpenPlanMemoryAsync();
        if (memory == null)
        {
            BubbleText = "还没有未完成的 plan 记忆。先在记忆里说“我计划……”，我就能陪你专注。";
            return;
        }

        _focusModeService.Start(memory);
        SetMonitorEnabled(true);
        BubbleText = $"已为「{memory.Title}」开启专注模式，我会轻轻提醒。";
    }

    [RelayCommand]
    private void Exit()
    {
        _exitApplication();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await SampleNowAsync();
    }

    private async Task SampleNowAsync()
    {
        var snapshot = _processMonitor.SampleForegroundWindowFocus();
        await ApplySnapshotAsync(snapshot);
    }

    private async Task ApplySnapshotAsync(ForegroundFocusSnapshot? snapshot)
    {
        if (!_settingsService.Settings.MonitorEnabled)
        {
            BubbleText = "Perelegans 先歇一会";
            return;
        }

        if (snapshot == null)
        {
            BubbleText = "正在等你打开下一个窗口";
            return;
        }

        CurrentProcessName = snapshot.ProcessName;
        CurrentDuration = snapshot.Duration;
        IsProductive = snapshot.IsKnownProductivityApp;

        var minutes = Math.Max(1, (int)Math.Round(snapshot.Duration.TotalMinutes));
        if (_focusModeService.IsActive)
        {
            ApplyFocusModeBubble(snapshot, minutes);
            return;
        }

        var latestPlan = await _databaseService.GetLatestOpenPlanMemoryAsync();
        if (latestPlan != null && !snapshot.IsKnownProductivityApp && minutes >= 8)
        {
            BubbleText = $"如果你愿意，等会儿可以回到「{latestPlan.Title}」。我会在旁边安静记着。";
            return;
        }

        BubbleText = snapshot.IsKnownProductivityApp
            ? $"{snapshot.ProcessName} 已陪你 {minutes} 分钟"
            : $"{snapshot.ProcessName} 停留 {minutes} 分钟";
    }

    private void ApplyFocusModeBubble(ForegroundFocusSnapshot snapshot, int minutes)
    {
        var relevant = IsProcessRelevantToFocus(snapshot.ProcessName, _focusModeService.TaskTags);
        if (relevant)
        {
            BubbleText = $"正在陪你推进「{_focusModeService.TaskTitle}」。";
            return;
        }

        BubbleText = minutes >= 3
            ? $"我先轻轻提醒一下：你在 {snapshot.ProcessName} 停了 {minutes} 分钟。要不要回到「{_focusModeService.TaskTitle}」？可以先做它的下一步。"
            : $"专注模式中：「{_focusModeService.TaskTitle}」。";
    }

    private void OnFocusModeStateChanged()
    {
        OnPropertyChanged(nameof(IsFocusModeActive));
        OnPropertyChanged(nameof(FocusModeMenuText));
        _ = SampleNowAsync();
    }

    private void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(IsMonitorEnabled));
        OnPropertyChanged(nameof(MonitorMenuText));
        _ = SampleNowAsync();
    }

    private void SetMonitorEnabled(bool enabled)
    {
        _settingsService.Settings.MonitorEnabled = enabled;
        _settingsService.Save();

        if (enabled)
        {
            _processMonitor.Start();
        }
        else
        {
            _processMonitor.Stop();
        }

        OnPropertyChanged(nameof(IsMonitorEnabled));
        OnPropertyChanged(nameof(MonitorMenuText));
        _ = SampleNowAsync();
    }

    private static bool IsProcessRelevantToFocus(string processName, string tags)
    {
        var process = processName.ToLowerInvariant();
        var normalizedTags = tags.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedTags))
        {
            return true;
        }

        if (ContainsAny(normalizedTags, "code", "development", "programming", "debug") &&
            ContainsAny(process, "code", "devenv", "rider", "visualstudio", "powershell", "terminal"))
        {
            return true;
        }

        if (ContainsAny(normalizedTags, "learn", "study", "deep learning", "dl", "ml") &&
            ContainsAny(process, "chrome", "msedge", "firefox", "code", "python", "jupyter", "obsidian", "notion"))
        {
            return true;
        }

        if (ContainsAny(normalizedTags, "writing", "notes") &&
            ContainsAny(process, "word", "onenote", "obsidian", "notion", "chrome", "msedge"))
        {
            return true;
        }

        if (ContainsAny(normalizedTags, "data", "analysis") &&
            ContainsAny(process, "excel", "powerbi", "python", "jupyter"))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _focusModeService.StateChanged -= OnFocusModeStateChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }
}
