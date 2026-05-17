using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class FloatingPetViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CodingClientCelebrationHold = TimeSpan.FromSeconds(3);

    private readonly ProcessMonitorService _processMonitor;
    private readonly FocusClassificationClient _focusClassificationClient;
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly FocusModeService _focusModeService;
    private readonly BreakpointSnapshotService _breakpointSnapshotService;
    private readonly CodingClientMonitorService _codingClientMonitorService;
    private readonly Action _showDashboard;
    private readonly Action<BreakpointSnapshot> _showBreakpointSnapshot;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _codingCelebrationTimer;
    private BreakpointSnapshot? _activeBreakpointSnapshot;
    private CodingClientActivitySnapshot? _codingClientSnapshot;
    private DateTime _codingClientCelebrationUntil = DateTime.MinValue;
    private string _codingClientCelebrationMessage = string.Empty;
    private bool _isAway;

    [ObservableProperty]
    private string _bubbleText = "Perelegans 正在待命";

    [ObservableProperty]
    private string _currentProcessName = string.Empty;

    [ObservableProperty]
    private TimeSpan _currentDuration;

    [ObservableProperty]
    private bool _isProductive;

    [ObservableProperty]
    private bool _hasBreakpointPrompt;

    [ObservableProperty]
    private string _petMood = "idle";

    [ObservableProperty]
    private bool _showCodingKeyboard;

    [ObservableProperty]
    private bool _showQuestionBadge;

    [ObservableProperty]
    private bool _showCelebrationMarks;

    public string BreakpointContinueText => "继续";

    public string SelectedPetSkinId => PetSkinPresets.Normalize(_settingsService.Settings.FloatingPetSkinId);

    public bool IsPinkPetSkinSelected => IsPetSkinSelected(PetSkinPresets.Pink);

    public bool IsWhiteOddEyesPetSkinSelected => IsPetSkinSelected(PetSkinPresets.WhiteOddEyes);

    public bool IsBlackPetSkinSelected => IsPetSkinSelected(PetSkinPresets.Black);

    public FloatingPetViewModel(
        ProcessMonitorService processMonitor,
        FocusClassificationClient focusClassificationClient,
        DatabaseService databaseService,
        SettingsService settingsService,
        FocusModeService focusModeService,
        BreakpointSnapshotService breakpointSnapshotService,
        CodingClientMonitorService codingClientMonitorService,
        Action showDashboard,
        Action<BreakpointSnapshot> showBreakpointSnapshot,
        Action openSettings,
        Action exitApplication)
    {
        _processMonitor = processMonitor;
        _focusClassificationClient = focusClassificationClient;
        _databaseService = databaseService;
        _settingsService = settingsService;
        _focusModeService = focusModeService;
        _breakpointSnapshotService = breakpointSnapshotService;
        _codingClientMonitorService = codingClientMonitorService;
        _showDashboard = showDashboard;
        _showBreakpointSnapshot = showBreakpointSnapshot;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        _codingCelebrationTimer = new DispatcherTimer
        {
            Interval = CodingClientCelebrationHold
        };
        _codingCelebrationTimer.Tick += OnCodingCelebrationTimerTick;
        _focusModeService.StateChanged += OnFocusModeStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _breakpointSnapshotService.BreakpointReady += OnBreakpointReady;
        _breakpointSnapshotService.AwayDetected += OnAwayDetected;
        _codingClientMonitorService.ActivityChanged += OnCodingClientActivityChanged;

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
    private void ContinueBreakpoint()
    {
        if (_activeBreakpointSnapshot == null)
        {
            _showDashboard();
            return;
        }

        var snapshot = _activeBreakpointSnapshot;
        _activeBreakpointSnapshot = null;
        HasBreakpointPrompt = false;
        UpdatePetMood();
        _showBreakpointSnapshot(snapshot);
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
            UpdatePetMood();
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
        UpdatePetMood();
        BubbleText = $"已为「{memory.Title}」开启专注模式，我会轻轻提醒。";
    }

    [RelayCommand]
    private void Exit()
    {
        _exitApplication();
    }

    [RelayCommand]
    private void SelectPetSkin(string? skinId)
    {
        var normalized = PetSkinPresets.Normalize(skinId);
        if (_settingsService.Settings.FloatingPetSkinId == normalized)
        {
            OnPetSkinSelectionChanged();
            return;
        }

        _settingsService.Settings.FloatingPetSkinId = normalized;
        _settingsService.Save();
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
        if (_isAway)
        {
            return;
        }

        if (HasBreakpointPrompt)
        {
            return;
        }

        if (!_settingsService.Settings.MonitorEnabled)
        {
            ResetCodingClientAdornments();
            PetMood = "sleep";
            BubbleText = "Perelegans 先歇一会";
            return;
        }

        if (TryApplyCodingClientSnapshot())
        {
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
            : $"专注模式中：\n「{_focusModeService.TaskTitle}」。";
    }

    private void OnFocusModeStateChanged()
    {
        OnPropertyChanged(nameof(IsFocusModeActive));
        OnPropertyChanged(nameof(FocusModeMenuText));
        UpdatePetMood();
        _ = SampleNowAsync();
    }

    private void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(IsMonitorEnabled));
        OnPropertyChanged(nameof(MonitorMenuText));
        OnPetSkinSelectionChanged();
        UpdatePetMood();
        _ = SampleNowAsync();
    }

    private void OnCodingClientActivityChanged(CodingClientActivitySnapshot snapshot)
    {
        _codingClientSnapshot = snapshot;
        if (snapshot.State == CodingClientActivityState.Idle)
        {
            if (TryApplyCodingClientSnapshot())
            {
                return;
            }

            ResetCodingClientAdornments();
            UpdatePetMood();
            _ = SampleNowAsync();
            return;
        }

        if (_isAway || HasBreakpointPrompt || !_settingsService.Settings.MonitorEnabled)
        {
            return;
        }

        TryApplyCodingClientSnapshot();
    }

    private void OnCodingCelebrationTimerTick(object? sender, EventArgs e)
    {
        _codingCelebrationTimer.Stop();
        _codingClientCelebrationUntil = DateTime.MinValue;
        _codingClientCelebrationMessage = string.Empty;
        if (_codingClientSnapshot?.State == CodingClientActivityState.Completed)
        {
            _codingClientSnapshot = null;
        }

        ResetCodingClientAdornments();
        PetMood = _focusModeService.IsActive ? "focus" : "idle";
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
            _focusModeService.Stop();
            _processMonitor.Stop();
        }

        OnPropertyChanged(nameof(IsMonitorEnabled));
        OnPropertyChanged(nameof(MonitorMenuText));
        UpdatePetMood();
        _ = SampleNowAsync();
    }

    private void OnAwayDetected()
    {
        _isAway = true;
        HasBreakpointPrompt = false;
        ResetCodingClientAdornments();
        PetMood = "sleep";
        BubbleText = "我先替你守住刚才的思路，等你回来。";
    }

    private void OnBreakpointReady(BreakpointSnapshot snapshot)
    {
        _isAway = false;
        _activeBreakpointSnapshot = snapshot;
        HasBreakpointPrompt = true;
        UpdatePetMood();
        BubbleText = string.IsNullOrWhiteSpace(snapshot.RelatedPlanTitle)
            ? $"欢迎回来。你离开前停在 {snapshot.ProcessName}，要不要接着刚才的思路？"
            : $"欢迎回来。你离开前可能在推进「{snapshot.RelatedPlanTitle}」，要不要继续？";
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

    private void UpdatePetMood()
    {
        if (_isAway)
        {
            ResetCodingClientAdornments();
            PetMood = "sleep";
            return;
        }

        if (!_settingsService.Settings.MonitorEnabled)
        {
            ResetCodingClientAdornments();
            PetMood = "sleep";
            return;
        }

        if (TryApplyCodingClientSnapshot())
        {
            return;
        }

        PetMood = _focusModeService.IsActive ? "focus" : "idle";
    }

    private bool TryApplyCodingClientSnapshot()
    {
        if (!IsCodingClientSnapshotActive(_codingClientSnapshot))
        {
            if (DateTime.Now <= _codingClientCelebrationUntil)
            {
                ShowCodingKeyboard = false;
                ShowQuestionBadge = false;
                ShowCelebrationMarks = true;
                PetMood = "celebrate";
                BubbleText = _codingClientCelebrationMessage;
                return true;
            }

            ResetCodingClientAdornments();
            return false;
        }

        ShowCodingKeyboard = false;
        ShowQuestionBadge = false;
        ShowCelebrationMarks = false;

        switch (_codingClientSnapshot!.State)
        {
            case CodingClientActivityState.Coding:
                _codingClientCelebrationUntil = DateTime.MinValue;
                _codingCelebrationTimer.Stop();
                PetMood = "coding";
                ShowCodingKeyboard = true;
                BubbleText = _codingClientSnapshot.Message;
                return true;
            case CodingClientActivityState.WaitingForConfirmation:
                _codingClientCelebrationUntil = DateTime.MinValue;
                _codingCelebrationTimer.Stop();
                PetMood = "question";
                ShowQuestionBadge = true;
                BubbleText = _codingClientSnapshot.Message;
                return true;
            case CodingClientActivityState.Completed:
                _codingClientCelebrationUntil = DateTime.Now + CodingClientCelebrationHold;
                _codingClientCelebrationMessage = _codingClientSnapshot.Message;
                _codingCelebrationTimer.Stop();
                _codingCelebrationTimer.Start();
                if (PetMood == "celebrate")
                {
                    PetMood = "idle";
                }

                PetMood = "celebrate";
                ShowCelebrationMarks = true;
                BubbleText = _codingClientSnapshot.Message;
                return true;
            default:
                ResetCodingClientAdornments();
                return false;
        }
    }

    private bool IsCodingClientSnapshotActive(CodingClientActivitySnapshot? snapshot)
    {
        return snapshot != null &&
               _settingsService.Settings.CodingClientMonitorEnabled &&
               IsCodingClientEnabled(snapshot.ClientKind) &&
               snapshot.State != CodingClientActivityState.Idle;
    }

    private bool IsCodingClientEnabled(CodingClientKind kind)
    {
        return kind switch
        {
            CodingClientKind.ClaudeDesktop => _settingsService.Settings.ClaudeDesktopMonitorEnabled,
            _ => _settingsService.Settings.CodexDesktopMonitorEnabled
        };
    }

    private void ResetCodingClientAdornments()
    {
        ShowCodingKeyboard = false;
        ShowQuestionBadge = false;
        ShowCelebrationMarks = false;
        if (DateTime.Now > _codingClientCelebrationUntil)
        {
            _codingClientCelebrationMessage = string.Empty;
        }
    }

    private bool IsPetSkinSelected(string skinId)
    {
        return SelectedPetSkinId == skinId;
    }

    private void OnPetSkinSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedPetSkinId));
        OnPropertyChanged(nameof(IsPinkPetSkinSelected));
        OnPropertyChanged(nameof(IsWhiteOddEyesPetSkinSelected));
        OnPropertyChanged(nameof(IsBlackPetSkinSelected));
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
        _breakpointSnapshotService.BreakpointReady -= OnBreakpointReady;
        _breakpointSnapshotService.AwayDetected -= OnAwayDetected;
        _codingClientMonitorService.ActivityChanged -= OnCodingClientActivityChanged;
        _codingCelebrationTimer.Stop();
    }
}
