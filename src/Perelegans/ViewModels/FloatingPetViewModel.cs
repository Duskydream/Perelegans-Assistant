using System.IO;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class FloatingPetViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CodingClientCelebrationHold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CodingReviewCardHold = TimeSpan.FromSeconds(30);

    private readonly ProcessMonitorService _processMonitor;
    private readonly FocusClassificationClient _focusClassificationClient;
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly FocusModeService _focusModeService;
    private readonly BreakpointSnapshotService _breakpointSnapshotService;
    private readonly CodingClientMonitorService _codingClientMonitorService;
    private readonly Action _showDashboard;
    private readonly Action _showMemoryReview;
    private readonly Action _showCompanionRoom;
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
    private int _pendingMemoryCount;

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
    private PetSpritePose _currentPetSpritePose = PetSpritePose.Idle;

    [ObservableProperty]
    private string _petMood = "idle";

    [ObservableProperty]
    private bool _showCodingKeyboard;

    [ObservableProperty]
    private bool _showQuestionBadge;

    [ObservableProperty]
    private bool _showCelebrationMarks;

    [ObservableProperty]
    private bool _hasCodingReviewCard;

    [ObservableProperty]
    private string _codingReviewTitle = string.Empty;

    [ObservableProperty]
    private string _codingReviewClientText = string.Empty;

    [ObservableProperty]
    private string _codingReviewChangedText = string.Empty;

    [ObservableProperty]
    private string _codingReviewRiskText = string.Empty;

    [ObservableProperty]
    private bool _hasPendingMemoryPrompt;

    public string BreakpointContinueText => "打开胶囊";

    public string MemoryReviewText => _pendingMemoryCount <= 1 ? "查看" : $"查看 {_pendingMemoryCount}";

    public string CodingReviewActionText => "打开验收";

    public string SelectedPetSkinId => PetSkinPresets.Normalize(_settingsService.Settings.FloatingPetSkinId);

    public bool IsPinkPetSkinSelected => IsPetSkinSelected(PetSkinPresets.Pink);

    public bool IsWhiteOddEyesPetSkinSelected => IsPetSkinSelected(PetSkinPresets.WhiteOddEyes);

    public bool IsBlackPetSkinSelected => IsPetSkinSelected(PetSkinPresets.Black);

    public bool IsCustomPetSkinSelected => IsPetSkinSelected(PetSkinPresets.Custom);

    public FloatingPetViewModel(
        ProcessMonitorService processMonitor,
        FocusClassificationClient focusClassificationClient,
        DatabaseService databaseService,
        SettingsService settingsService,
        FocusModeService focusModeService,
        BreakpointSnapshotService breakpointSnapshotService,
        CodingClientMonitorService codingClientMonitorService,
        Action showDashboard,
        Action showMemoryReview,
        Action showCompanionRoom,
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
        _showMemoryReview = showMemoryReview;
        _showCompanionRoom = showCompanionRoom;
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

    public AppSettings Settings => _settingsService.Settings;

    public event Action? SettingsChanged
    {
        add => _settingsService.SettingsChanged += value;
        remove => _settingsService.SettingsChanged -= value;
    }

    public string MonitorMenuText => IsMonitorEnabled ? "先歇一会" : "开始陪伴";

    public bool IsFocusModeActive => _focusModeService.IsActive;

    public string FocusModeMenuText => IsFocusModeActive ? "结束专注" : "专注模式";

    public void SetPetSpritePose(PetSpritePose pose)
    {
        CurrentPetSpritePose = pose;
        PetMood = pose switch
        {
            PetSpritePose.Paused => "sleep",
            PetSpritePose.Focus => "focus",
            PetSpritePose.Breakpoint => "question",
            _ => "idle"
        };
    }

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
    private void OpenMemoryReview()
    {
        HasPendingMemoryPrompt = false;
        _showMemoryReview();
    }

    [RelayCommand]
    private void OpenCompanionRoom()
    {
        _showCompanionRoom();
    }

    [RelayCommand]
    private void OpenCodingReview()
    {
        if (_codingClientSnapshot != null && TryActivateCodingClient(_codingClientSnapshot.ClientKind))
        {
            HasCodingReviewCard = false;
            return;
        }

        BubbleText = "我没找到对应的编码窗口。先把 Codex / Claude Code / OpenCode 打开到前台一次，我再帮你跳过去。";
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
            var latestTask = (await _databaseService.GetFocusTasksAsync())
                .Where(task => task.Status == FocusTaskStatus.Active)
                .OrderByDescending(task => task.CreatedAt)
                .FirstOrDefault();
            if (latestTask == null)
            {
                BubbleText = "还没有未完成的 plan 或最新任务。先在对话里写一句“我要……”，我就能接上那条任务陪你专注。";
                return;
            }

            _focusModeService.Start(
                latestTask.Title,
                latestTask.Tags,
                latestTask.NextAction);
            SetMonitorEnabled(true);
            UpdatePetMood();
            BubbleText = $"已接上最新任务「{latestTask.Title}」，专注模式开启。";
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

        var pendingCount = await _databaseService.GetPendingContextMemoryCountAsync();
        if (pendingCount > 0 && !IsCodingClientSnapshotActive(_codingClientSnapshot))
        {
            _pendingMemoryCount = pendingCount;
            OnPropertyChanged(nameof(MemoryReviewText));
            HasPendingMemoryPrompt = true;
            ResetCodingClientAdornments();
            SetPetSpritePose(PetSpritePose.Breakpoint);
            BubbleText = pendingCount == 1
                ? "我发现 1 条可能值得留下的记忆，等你确认后再放进星图。"
                : $"我发现 {pendingCount} 条候选记忆，等你确认后再放进星图。";
            return;
        }

        HasPendingMemoryPrompt = false;

        if (!_settingsService.Settings.MonitorEnabled)
        {
            ResetCodingClientAdornments();
            SetPetSpritePose(PetSpritePose.Paused);
            BubbleText = "Perelegans 先歇一会";
            return;
        }

        if (TryApplyCodingClientSnapshot())
        {
            return;
        }

        if (snapshot == null)
        {
            SetPetSpritePose(PetSpritePose.Idle);
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
            SetPetSpritePose(PetSpritePose.Distracted);
            BubbleText = $"如果你愿意，等会儿可以回到「{latestPlan.Title}」。我会在旁边安静记着。";
            return;
        }

        SetPetSpritePose(snapshot.IsKnownProductivityApp
            ? PetSpritePose.Productive
            : PetSpritePose.Distracted);
        BubbleText = snapshot.IsKnownProductivityApp
            ? $"{snapshot.ProcessName} 已陪你 {minutes} 分钟"
            : $"{snapshot.ProcessName} 停留 {minutes} 分钟";
    }

    private void ApplyFocusModeBubble(ForegroundFocusSnapshot snapshot, int minutes)
    {
        var relevant = IsProcessRelevantToFocus(snapshot.ProcessName, _focusModeService.TaskTags);
        if (relevant)
        {
            SetPetSpritePose(PetSpritePose.Focus);
            BubbleText = $"正在陪你推进「{_focusModeService.TaskTitle}」。";
            return;
        }

        SetPetSpritePose(PetSpritePose.Distracted);
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
        SetPetSpritePose(_focusModeService.IsActive ? PetSpritePose.Focus : PetSpritePose.Idle);
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
        SetPetSpritePose(PetSpritePose.Paused);
        BubbleText = "我先替你守住刚才的思路，等你回来。";
    }

    private void OnBreakpointReady(BreakpointSnapshot snapshot)
    {
        _isAway = false;
        _activeBreakpointSnapshot = snapshot;
        HasBreakpointPrompt = true;
        SetPetSpritePose(PetSpritePose.Breakpoint);
        BubbleText = string.IsNullOrWhiteSpace(snapshot.RelatedPlanTitle)
            ? $"欢迎回来。我把你离开前的 {snapshot.ProcessName} 现场收成一颗恢复胶囊了。"
            : $"欢迎回来。我把「{snapshot.RelatedPlanTitle}」的断点收成一颗恢复胶囊了。";
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
            SetPetSpritePose(PetSpritePose.Paused);
            return;
        }

        if (!_settingsService.Settings.MonitorEnabled)
        {
            ResetCodingClientAdornments();
            SetPetSpritePose(PetSpritePose.Paused);
            return;
        }

        if (TryApplyCodingClientSnapshot())
        {
            return;
        }

        SetPetSpritePose(_focusModeService.IsActive ? PetSpritePose.Focus : PetSpritePose.Idle);
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
                HasCodingReviewCard = true;
                CurrentPetSpritePose = PetSpritePose.Productive;
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
                HasCodingReviewCard = false;
                CurrentPetSpritePose = PetSpritePose.Focus;
                PetMood = "coding";
                ShowCodingKeyboard = true;
                BubbleText = _codingClientSnapshot.Message;
                return true;
            case CodingClientActivityState.WaitingForConfirmation:
                _codingClientCelebrationUntil = DateTime.MinValue;
                _codingCelebrationTimer.Stop();
                HasCodingReviewCard = false;
                CurrentPetSpritePose = PetSpritePose.Breakpoint;
                PetMood = "question";
                ShowQuestionBadge = true;
                BubbleText = _codingClientSnapshot.Message;
                return true;
            case CodingClientActivityState.Completed:
                _codingClientCelebrationUntil = DateTime.Now + CodingReviewCardHold;
                ApplyCodingReviewCard(_codingClientSnapshot);
                _codingClientCelebrationMessage = "AI 写完了，先验收再合并。";
                _codingCelebrationTimer.Stop();
                _codingCelebrationTimer.Interval = CodingReviewCardHold;
                _codingCelebrationTimer.Start();
                if (PetMood == "celebrate")
                {
                    PetMood = "idle";
                }

                CurrentPetSpritePose = PetSpritePose.Productive;
                PetMood = "celebrate";
                ShowCelebrationMarks = true;
                BubbleText = _codingClientCelebrationMessage;
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

    private void ApplyCodingReviewCard(CodingClientActivitySnapshot snapshot)
    {
        var changedPath = snapshot.LastChangedPath.Trim();
        var extension = Path.GetExtension(changedPath).ToLowerInvariant();
        CodingReviewTitle = "AI 编码完成验收";
        CodingReviewClientText = snapshot.ClientName;
        CodingReviewChangedText = string.IsNullOrWhiteSpace(changedPath)
            ? "最近变更：未捕捉到明确文件"
            : $"最近变更：{ShortenPath(changedPath)}";
        CodingReviewRiskText = extension switch
        {
            ".xaml" => "先看绑定、资源键和布局是否被挤爆。",
            ".cs" => "先看空值、异步取消、事件订阅和数据库写入。",
            ".resx" => "先看资源键是否成对存在，中文是否乱码。",
            ".csproj" => "先看包引用、目标框架和发布配置。",
            _ => "先确认变更范围，再跑一次验证。"
        };
        HasCodingReviewCard = true;
    }

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(path);
        var directory = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory}\\{fileName}";
    }

    private bool IsCodingClientEnabled(CodingClientKind kind)
    {
        return kind switch
        {
            CodingClientKind.ClaudeDesktop => _settingsService.Settings.ClaudeDesktopMonitorEnabled,
            CodingClientKind.OpenCodeDesktop => _settingsService.Settings.OpenCodeDesktopMonitorEnabled,
            _ => _settingsService.Settings.CodexDesktopMonitorEnabled
        };
    }

    private static bool TryActivateCodingClient(CodingClientKind kind)
    {
        var candidates = kind switch
        {
            CodingClientKind.ClaudeDesktop => new[] { "Claude", "Claude Code", "claude" },
            CodingClientKind.OpenCodeDesktop => new[] { "OpenCode", "opencode", "ai.opencode.desktop" },
            _ => new[] { "Codex", "Codex Desktop", "codex" }
        };

        foreach (var candidate in candidates)
        {
            foreach (var process in Process.GetProcessesByName(candidate))
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    ShowWindow(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
                catch
                {
                }
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    var name = process.ProcessName;
                    var title = process.MainWindowTitle;
                    if (!candidates.Any(candidate =>
                            name.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
                            title.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    ShowWindow(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void ResetCodingClientAdornments()
    {
        ShowCodingKeyboard = false;
        ShowQuestionBadge = false;
        ShowCelebrationMarks = false;
        HasCodingReviewCard = false;
        if (DateTime.Now > _codingClientCelebrationUntil)
        {
            _codingClientCelebrationMessage = string.Empty;
            CodingReviewTitle = string.Empty;
            CodingReviewClientText = string.Empty;
            CodingReviewChangedText = string.Empty;
            CodingReviewRiskText = string.Empty;
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
        OnPropertyChanged(nameof(IsCustomPetSkinSelected));
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
