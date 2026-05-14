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
    private string _bubbleText = "Perelegans \u6b63\u5728\u5f85\u547d";

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

        _ = SampleNowAsync();
    }

    public bool IsMonitorEnabled => _settingsService.Settings.MonitorEnabled;

    public string MonitorMenuText => IsMonitorEnabled ? "\u5148\u6b47\u4e00\u4f1a" : "\u5f00\u59cb\u966a\u4f34";

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
        _settingsService.Settings.MonitorEnabled = !_settingsService.Settings.MonitorEnabled;
        _settingsService.Save();

        if (_settingsService.Settings.MonitorEnabled)
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
            BubbleText = "Perelegans \u5148\u6b47\u4e00\u4f1a";
            return;
        }

        if (snapshot == null)
        {
            BubbleText = "\u6b63\u5728\u7b49\u4f60\u6253\u5f00\u4e0b\u4e00\u4e2a\u7a97\u53e3";
            return;
        }

        CurrentProcessName = snapshot.ProcessName;
        CurrentDuration = snapshot.Duration;
        IsProductive = snapshot.IsKnownProductivityApp;

        var minutes = Math.Max(1, (int)Math.Round(snapshot.Duration.TotalMinutes));
        if (_focusModeService.IsActive)
        {
            var relevant = IsProcessRelevantToFocus(snapshot.ProcessName, _focusModeService.TaskTags);
            if (!relevant && minutes >= 3)
            {
                BubbleText = $"我先轻轻提醒一下：现在在 {snapshot.ProcessName} 停了 {minutes} 分钟，和「{_focusModeService.TaskTitle}」好像关系不大。要不要慢慢回到任务？";
                return;
            }

            BubbleText = relevant
                ? $"正在陪你推进「{_focusModeService.TaskTitle}」"
                : $"专注模式中：{_focusModeService.TaskTitle}";
            return;
        }

        var latestPlan = await _databaseService.GetLatestOpenPlanMemoryAsync();
        if (latestPlan != null && !snapshot.IsKnownProductivityApp && minutes >= 8)
        {
            BubbleText = $"如果你愿意，等会儿可以回到「{latestPlan.Title}」。我会在旁边安静记着。";
            return;
        }

        BubbleText = snapshot.IsKnownProductivityApp
            ? $"{snapshot.ProcessName} \u5df2\u966a\u4f60 {minutes} \u5206\u949f"
            : $"{snapshot.ProcessName} \u505c\u7559 {minutes} \u5206\u949f";
    }

    private void OnFocusModeStateChanged()
    {
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
    }
}
