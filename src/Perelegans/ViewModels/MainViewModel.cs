using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Perelegans.Models;
using Perelegans.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace Perelegans.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxDefaultMemoryNodes = 180;
    private const int MaxFilteredMemoryNodes = 260;
    private const int MaxInteractiveLinkRefreshNodes = 90;

    private readonly DatabaseService _dbService;
    private readonly SettingsService _settingsService;
    private readonly ProcessMonitorService _processMonitor;
    private readonly FocusClassificationClient? _focusClient;
    private readonly ContextRetrievalService _contextRetrievalService;
    private readonly MemoryExtractionService _memoryExtractionService;
    private readonly FocusModeService _focusModeService;
    private readonly CodingClientMonitorService _codingClientMonitorService;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly DispatcherTimer _assistantThinkingTimer;
    private int _assistantThinkingIndex;
    private DateTime _lastSceneCheckpointAt = DateTime.MinValue;
    private string _lastSceneCheckpointProcess = string.Empty;
    private CancellationTokenSource? _assistantResponseCancellation;
    private CodingClientActivitySnapshot? _latestCodingClientSnapshot;
    private string _focusInterventionProcess = string.Empty;
    private int _focusInterventionLevel;
    private Dictionary<int, string> _memorySearchIndex = [];
    private string _expandedMemoryConstellation = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ApplicationUsage> _applications = new();

    [ObservableProperty]
    private ObservableCollection<ApplicationUsageSession> _recentSessions = new();

    [ObservableProperty]
    private string _currentProcessName = TranslationService.Instance["Main_WaitingForApp"];

    [ObservableProperty]
    private string _currentProcessDurationText = TranslationService.Instance["Main_ZeroDuration"];

    [ObservableProperty]
    private string _currentFocusLabel = TranslationService.Instance["Main_Unknown"];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isAssistantThinking;

    [ObservableProperty]
    private string _assistantThinkingText = TranslationService.Instance["Main_AssistantThinking1"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendConversationMessageCommand))]
    private string _conversationInput = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ConversationMessage> _conversationMessages = new();

    [ObservableProperty]
    private ObservableCollection<FocusTask> _focusTasks = new();

    [ObservableProperty]
    private ObservableCollection<ContextMemory> _contextMemories = new();

    [ObservableProperty]
    private ObservableCollection<ContextMemory> _pendingContextMemories = new();

    [ObservableProperty]
    private ObservableCollection<string> _availableGalaxyTags = new();

    [ObservableProperty]
    private ObservableCollection<string> _availableGalaxyGroups = new();

    [ObservableProperty]
    private ObservableCollection<GalaxyLinkViewModel> _galaxyLinks = new();

    [ObservableProperty]
    private ObservableCollection<MemoryConstellationNodeViewModel> _memoryConstellations = new();

    [ObservableProperty]
    private ObservableCollection<FishboneBranchViewModel> _fishboneBranches = new();

    [ObservableProperty]
    private string _memoryPreviewMode = "galaxy";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGalaxyTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteGalaxyTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSelectedMemoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RejectSelectedMemoryCommand))]
    private ContextMemory? _selectedGalaxyMemory;

    [ObservableProperty]
    private string _galaxyEditTitle = string.Empty;

    [ObservableProperty]
    private string _galaxyEditTags = string.Empty;

    [ObservableProperty]
    private string _galaxyEditGroup = string.Empty;

    [ObservableProperty]
    private string _galaxyEditCreatedAtText = string.Empty;

    [ObservableProperty]
    private bool _galaxyEditIsCompleted;

    [ObservableProperty]
    private bool _galaxyEditIsAbandoned;

    [ObservableProperty]
    private string _currentFocusGoal = string.Empty;

    [ObservableProperty]
    private bool _isGalaxyVisible;

    [ObservableProperty]
    private bool _hasConversationStarted;

    [ObservableProperty]
    private bool _isFocusModeActive;

    [ObservableProperty]
    private string _galaxySearchText = string.Empty;

    [ObservableProperty]
    private string _galaxyTagFilter = string.Empty;

    [ObservableProperty]
    private string _galaxyGroupFilter = string.Empty;

    private List<ContextMemory> _allVisibleContextMemories = [];

    public int ApplicationCount => Applications.Count;
    public int ProductiveCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Productive);
    public int DistractingCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Distracting);
    public int UnknownCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Unknown);
    public string TotalDurationText => FormatDuration(Applications.Aggregate(TimeSpan.Zero, (total, item) => total + item.TotalDuration));
    public string RecentSessionCountText => RecentSessions.Count.ToString();
    public bool IsMonitorEnabled => _settingsService.Settings.MonitorEnabled;
    public string MonitorButtonText => IsMonitorEnabled ? T("Main_PauseMonitor") : T("Main_StartMonitor");
    public string MonitoringStateText => IsMonitorEnabled ? T("Main_MonitoringOn") : T("Main_MonitoringOff");
    public string CurrentFocusGoalDisplay
    {
        get
        {
            if (_focusModeService.IsActive && !string.IsNullOrWhiteSpace(_focusModeService.TaskTitle))
            {
                return _focusModeService.TaskTitle;
            }

            var latestPlan = _allVisibleContextMemories
                .Where(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
                .OrderByDescending(memory => memory.UpdatedAt)
                .FirstOrDefault();
            return latestPlan?.Title ?? T("Main_NoTask");
        }
    }
    public string ApplicationCountText => string.Format(T("Main_AppsCountFormat"), ApplicationCount);
    public string FocusTaskCountText => string.Format(T("Main_FocusTaskCountFormat"), FocusTasks.Count(t => t.Status == FocusTaskStatus.Completed), FocusTasks.Count);
    public string MemoryCountText
    {
        get
        {
            var total = string.Format(T("Main_MemoryCountFormat"), _allVisibleContextMemories.Count);
            var rendered = IsMemoryConstellationLayer
                ? $" · 星座 {MemoryConstellations.Count}"
                : _allVisibleContextMemories.Count > 0 && ContextMemories.Count < _allVisibleContextMemories.Count
                ? $" · 显示 {ContextMemories.Count}"
                : string.Empty;
            var pending = PendingContextMemories.Count > 0
                ? $" · {PendingContextMemories.Count} 待确认"
                : string.Empty;
            return total + rendered + pending;
        }
    }
    public bool HasPendingMemories => PendingContextMemories.Count > 0;
    public string PendingMemoryCountText => PendingContextMemories.Count == 0
        ? "没有待确认记忆"
        : $"{PendingContextMemories.Count} 条待确认记忆";
    public bool HasSelectedGalaxyTask => SelectedGalaxyMemory != null;
    public bool HasSelectedPendingMemory => SelectedGalaxyMemory?.IsPendingReview == true;
    public bool HasGalaxyFilters => !string.IsNullOrWhiteSpace(NormalizeGalaxyFilter(GalaxySearchText, "search")) ||
        !string.IsNullOrWhiteSpace(NormalizeGalaxyFilter(GalaxyTagFilter, "tag")) ||
        !string.IsNullOrWhiteSpace(NormalizeGalaxyFilter(GalaxyGroupFilter, "group"));
    public bool IsGalaxyPreviewMode => MemoryPreviewMode == "galaxy";
    public bool IsMemoryConstellationLayer => IsGalaxyPreviewMode && !HasGalaxyFilters;
    public bool IsMemoryNodeLayer => IsGalaxyPreviewMode && !IsMemoryConstellationLayer;
    public bool IsGalaxyEmpty => IsMemoryConstellationLayer
        ? MemoryConstellations.Count == 0
        : ContextMemories.Count == 0;
    public bool IsFishbonePreviewMode => MemoryPreviewMode == "fishbone";
    public string FocusModeButtonText => IsFocusModeActive ? T("Main_FocusModeEnd") : T("Main_FocusModeStart");
    public string FocusModeStatusText => IsFocusModeActive
        ? string.Format(T("Main_FocusModeActiveFormat"), _focusModeService.TaskTitle)
        : T("Main_FocusModeIdle");
    public string ProductiveShareText
    {
        get
        {
            if (ApplicationCount == 0)
            {
                return T("Main_NoSignal");
            }

            var share = (int)Math.Round(ProductiveCount * 100d / ApplicationCount);
            return string.Format(T("Main_ProductiveShareFormat"), share);
        }
    }

    public MainViewModel(
        DatabaseService dbService,
        SettingsService settingsService,
        ProcessMonitorService processMonitor,
        FocusClassificationClient? focusClient,
        ContextRetrievalService contextRetrievalService,
        MemoryExtractionService memoryExtractionService,
        FocusModeService focusModeService,
        CodingClientMonitorService codingClientMonitorService,
        Action openSettings,
        Action exitApplication)
    {
        _dbService = dbService;
        _settingsService = settingsService;
        _processMonitor = processMonitor;
        _focusClient = focusClient;
        _contextRetrievalService = contextRetrievalService;
        _memoryExtractionService = memoryExtractionService;
        _focusModeService = focusModeService;
        _codingClientMonitorService = codingClientMonitorService;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _assistantThinkingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.4)
        };
        _assistantThinkingTimer.Tick += (_, _) => AdvanceAssistantThinkingText();

        _processMonitor.ForegroundFocusUpdated += OnForegroundFocusUpdated;
        _focusModeService.StateChanged += OnFocusModeStateChanged;
        _codingClientMonitorService.ActivityChanged += OnCodingClientActivityChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        IsFocusModeActive = _focusModeService.IsActive;
        _latestCodingClientSnapshot = _codingClientMonitorService.CurrentSnapshot;
        TranslationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                if (CurrentProcessName == "Waiting for foreground app" || CurrentProcessName == "等待前台应用")
                {
                    CurrentProcessName = T("Main_WaitingForApp");
                }

                if (CurrentFocusLabel == "Unknown" || CurrentFocusLabel == "未知")
                {
                    CurrentFocusLabel = T("Main_Unknown");
                }

                RefreshComputedStats();
                RefreshUiTextProperties();
            }
        };

        SyncFocusGoalFromSettings();
        SeedConversation();
    }

    public async Task InitializeAsync()
    {
        await _dbService.EnsureDatabaseCreatedAsync();
        await RefreshAsync();
        await RefreshFocusTasksAsync();
        await RefreshContextMemoriesAsync();

        _processMonitor.SetInterval(_settingsService.Settings.MonitorIntervalSeconds);
        if (_settingsService.Settings.MonitorEnabled)
        {
            _processMonitor.Start();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Applications = new ObservableCollection<ApplicationUsage>(await _dbService.GetAllApplicationUsagesAsync());
            RecentSessions = new ObservableCollection<ApplicationUsageSession>(
                (await _dbService.GetAllApplicationUsageSessionsAsync()).Take(80));
            StatusText = string.Format(T("Main_LastRefreshedFormat"), DateTime.Now.ToString("HH:mm:ss"));
            RefreshComputedStats();
            if (IsStatisticsVisible)
            {
                await RefreshUsageStatisticsAsync();
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearUsageData()
    {
        var result = MessageBox.Show(
            T("Main_ClearDataConfirm"),
            T("Main_WindowTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await _dbService.ClearApplicationUsageDataAsync();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveBackup()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "SQLite Database (*.db)|*.db",
            FileName = "focusarchive_backup.db"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _dbService.BackupDatabaseAsync(dialog.FileName);
        StatusText = T("Main_BackupSaved");
    }

    [RelayCommand]
    private async Task RestoreBackup()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQLite Database (*.db)|*.db"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var shouldResumeMonitor = _processMonitor.IsRunning;
        if (shouldResumeMonitor)
        {
            await _processMonitor.StopAsync();
        }

        await _dbService.RestoreDatabaseAsync(dialog.FileName);
        await RefreshAsync();

        if (shouldResumeMonitor && _settingsService.Settings.MonitorEnabled)
        {
            _processMonitor.Start();
        }

        StatusText = T("Main_BackupRestored");
    }

    [RelayCommand]
    private void ToggleMonitor()
    {
        var settings = _settingsService.Settings;
        settings.MonitorEnabled = !settings.MonitorEnabled;
        _settingsService.Save();

        if (settings.MonitorEnabled)
        {
            _processMonitor.Start();
        }
        else
        {
            _processMonitor.Stop();
        }

        OnPropertyChanged(nameof(IsMonitorEnabled));
        OnPropertyChanged(nameof(MonitorButtonText));
        OnPropertyChanged(nameof(MonitoringStateText));
    }

    [RelayCommand]
    private async Task ToggleFocusMode()
    {
        if (_focusModeService.IsActive)
        {
            _focusModeService.Stop();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_FocusModeEnded")));
            return;
        }

        var memory = await _dbService.GetLatestOpenPlanMemoryAsync();
        if (memory == null)
        {
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_FocusModeNoPlan")));
            return;
        }

        _focusModeService.Start(memory);
        SetMonitorEnabled(true);
        ConversationMessages.Add(ConversationMessage.Assistant(
            string.Format(T("Main_FocusModeStartedFormat"), memory.Title)));
    }

    [RelayCommand]
    private void SetMemoryPreviewMode(string mode)
    {
        MemoryPreviewMode = mode == "fishbone" ? "fishbone" : "galaxy";
    }

    [RelayCommand]
    private void ClearGalaxyFilters()
    {
        _expandedMemoryConstellation = string.Empty;
        SelectedGalaxyMemory = null;
        GalaxySearchText = string.Empty;
        GalaxyTagFilter = string.Empty;
        GalaxyGroupFilter = string.Empty;
        ApplyGalaxyMemoryFilters();
        OnPropertyChanged(nameof(HasGalaxyFilters));
    }

    [RelayCommand]
    private void ShowMemoryConstellationOverview()
    {
        MemoryPreviewMode = "galaxy";
        ClearGalaxyFilters();
    }

    [RelayCommand]
    private void OpenMemoryConstellation(MemoryConstellationNodeViewModel? constellation)
    {
        if (constellation == null)
        {
            return;
        }

        MemoryPreviewMode = "galaxy";
        _expandedMemoryConstellation = constellation.Title;
        GalaxySearchText = string.Empty;
        GalaxyTagFilter = string.Empty;
        GalaxyGroupFilter = constellation.Title;
        SelectedGalaxyMemory = ContextMemories
            .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .ThenByDescending(memory => memory.Weight)
            .FirstOrDefault();
        StatusText = $"已展开分组：{constellation.Title}";
    }

    public void OpenMemoryReview()
    {
        IsGalaxyVisible = true;
        IsStatisticsVisible = false;
        if (PendingContextMemories.Count > 0)
        {
            SelectedGalaxyMemory = PendingContextMemories[0];
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _openSettings();
        SyncFocusGoalFromSettings();
        _ = RefreshContextMemoriesAsync();
    }

    [RelayCommand]
    private void ExitApp()
    {
        _exitApplication();
    }

    private void OnForegroundFocusUpdated(ForegroundFocusSnapshot snapshot)
    {
        CurrentProcessName = snapshot.ProcessName;
        CurrentProcessDurationText = FormatDuration(snapshot.Duration);
        CurrentFocusLabel = snapshot.IsKnownProductivityApp ? T("Main_LikelyProductive") : T("Main_Unclassified");
        QueueSceneCheckpoint(snapshot);
        EvaluateFocusModeIntervention(snapshot);
    }

    private void OnCodingClientActivityChanged(CodingClientActivitySnapshot snapshot)
    {
        _latestCodingClientSnapshot = snapshot;
    }

    private void EvaluateFocusModeIntervention(ForegroundFocusSnapshot snapshot)
    {
        if (!_focusModeService.IsActive)
        {
            ResetFocusIntervention();
            return;
        }

        if (IsProcessRelevantToCurrentFocus(snapshot))
        {
            ResetFocusIntervention();
            return;
        }

        if (!string.Equals(_focusInterventionProcess, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            _focusInterventionProcess = snapshot.ProcessName;
            _focusInterventionLevel = 0;
        }

        var minutes = snapshot.Duration.TotalMinutes;
        var level = minutes >= 15 ? 3 : minutes >= 8 ? 2 : minutes >= 3 ? 1 : 0;
        if (level <= 0 || level <= _focusInterventionLevel)
        {
            return;
        }

        _focusInterventionLevel = level;
        var message = CreateFocusInterventionMessage(level, snapshot);
        ConversationMessages.Add(ConversationMessage.Assistant(message));
        StatusText = level switch
        {
            1 => "专注模式：轻提醒已送达",
            2 => "专注模式：给出下一步",
            _ => "专注模式：等待你决定是否切回"
        };
    }

    private void ResetFocusIntervention()
    {
        _focusInterventionProcess = string.Empty;
        _focusInterventionLevel = 0;
    }

    private bool IsProcessRelevantToCurrentFocus(ForegroundFocusSnapshot snapshot)
    {
        var processTags = InferProcessTags(snapshot.ProcessName);
        var focusTerms = SplitTagsForInsight(_focusModeService.TaskTags)
            .Concat(SplitTagsForInsight(_focusModeService.TaskTitle))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (focusTerms.Count == 0)
        {
            return snapshot.IsKnownProductivityApp;
        }

        if (processTags.Overlaps(focusTerms))
        {
            return true;
        }

        var processName = snapshot.ProcessName.ToLowerInvariant();
        if (ContainsAny(processName, "codex", "claude", "code", "devenv", "rider", "visualstudio") &&
            focusTerms.Overlaps(["code", "coding", "development", "desktop", "wpf", "frontend", "backend", "bug", "fix"]))
        {
            return true;
        }

        return false;
    }

    private string CreateFocusInterventionMessage(int level, ForegroundFocusSnapshot snapshot)
    {
        var task = string.IsNullOrWhiteSpace(_focusModeService.TaskTitle)
            ? "当前 plan"
            : _focusModeService.TaskTitle;
        var nextAction = string.IsNullOrWhiteSpace(_focusModeService.NextAction)
            ? $"先回到「{task}」，只做一个 10 分钟内能完成的小动作。"
            : _focusModeService.NextAction.Trim();
        var minutes = Math.Max(1, (int)Math.Round(snapshot.Duration.TotalMinutes));

        return level switch
        {
            1 => $"我看到你在 {snapshot.ProcessName} 停了约 {minutes} 分钟，可能已经离开「{task}」一点点了。先不打断你，只轻轻把主线放在这里。",
            2 => $"你已经在 {snapshot.ProcessName} 待了约 {minutes} 分钟。如果这是查资料，可以继续；如果不是，我们可以从这一步切回：{nextAction}",
            _ => $"这个岔路已经持续约 {minutes} 分钟了。要不要做个选择：暂停「{task}」、把它标记为放弃，或者现在切回？我建议先切回这一步：{nextAction}"
        };
    }

    [RelayCommand(CanExecute = nameof(CanSendConversationMessage))]
    private async Task SendConversationMessage()
    {
        var text = ConversationInput.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ConversationMessages.Add(ConversationMessage.User(text));
        ConversationInput = string.Empty;
        HasConversationStarted = true;
        await DispatchConversationAsync(text);
    }

    [RelayCommand]
    private void UseGoalPreset(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        ConversationInput = preset;
    }

    [RelayCommand]
    private void ToggleGalaxy()
    {
        IsGalaxyVisible = !IsGalaxyVisible;
        if (IsGalaxyVisible)
        {
            IsStatisticsVisible = false;
        }
    }

    [RelayCommand]
    private async Task GenerateDailyReview()
    {
        HasConversationStarted = true;
        StartAssistantThinking();
        _assistantResponseCancellation?.Cancel();
        using var responseCancellation = new CancellationTokenSource();
        _assistantResponseCancellation = responseCancellation;
        StatusText = T("Main_DailyReviewGenerating");
        try
        {
            var snapshot = _processMonitor.SampleForegroundWindowFocus();
            var memories = await _contextRetrievalService.RetrieveAsync("today local memory context recap plan completed process switches", snapshot, 18);
            var sessions = await _dbService.GetApplicationUsageSessionsSinceAsync(DateTime.Now.AddHours(-24));
            DailyReviewDraft? review = null;
            if (_focusClient?.IsConfigured == true)
            {
                try
                {
                    review = await _focusClient.CreateDailyReviewAsync(FocusTasks, memories, sessions, responseCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    StatusText = T("Main_AssistantStopped");
                    return;
                }
                catch (Exception ex)
                {
                    App.WriteCrashLog(ex);
                }
            }

            var insightMemories = memories
                .Concat((await _dbService.GetContextMemoriesAsync())
                    .Where(memory => memory.IsPlan)
                    .OrderByDescending(memory => memory.UpdatedAt)
                    .Take(12))
                .DistinctBy(memory => memory.Id)
                .ToList();
            var insight = await CreateDesktopInsightAsync("daily_review", "每日总结：生成计划推断、鱼骨归因和星图解释", insightMemories, sessions, snapshot, responseCancellation.Token);
            var archivePath = await ArchiveLocalContextDigestAsync("daily", insightMemories, sessions, insight);
            await _dbService.RefreshMemoryLifecycleForDailyReviewAsync();
            await RefreshContextMemoriesAsync();

            review ??= CreateFallbackDailyReview();
            var reviewText = FormatDailyReview(review) +
                "\n\n我也把今天能用的上下文悄悄存好了。等你愿意的时候，可以只回答一个小问题：明天开场时，哪件事最值得先开始？";
            var statsSnapshot = CreateDailyReviewUsageStatsSnapshot(sessions);
            ConversationMessages.Add(statsSnapshot.HasSlices
                ? ConversationMessage.AssistantWithUsageStats(reviewText, statsSnapshot)
                : ConversationMessage.Assistant(reviewText));
            StatusText = T("Main_DailyReviewReady");
        }
        finally
        {
            StopAssistantThinking();
            if (_assistantResponseCancellation == responseCancellation)
            {
                _assistantResponseCancellation = null;
            }
        }
    }

    [RelayCommand]
    private void SelectGalaxyTask(ContextMemory? memory)
    {
        SelectedGalaxyMemory = memory;
    }

    [RelayCommand(CanExecute = nameof(CanEditGalaxyTask))]
    private async Task SaveGalaxyTask()
    {
        if (SelectedGalaxyMemory == null)
        {
            return;
        }

        var title = GalaxyEditTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusText = T("Main_GalaxyTaskTitleRequired");
            return;
        }

        var updated = await _dbService.UpsertContextMemoryAsync(
            title,
            SelectedGalaxyMemory.Content,
            SelectedGalaxyMemory.Type,
            SelectedGalaxyMemory.Source,
            GalaxyEditTags,
            SelectedGalaxyMemory.Weight,
            SelectedGalaxyMemory.Id,
            memoryAxis: SelectedGalaxyMemory.MemoryAxis,
            aiDescription: SelectedGalaxyMemory.AiDescription,
            aiExplanation: SelectedGalaxyMemory.AiExplanation,
            nextPrediction: SelectedGalaxyMemory.NextPrediction,
            isPlan: SelectedGalaxyMemory.IsPlan,
            isCompleted: SelectedGalaxyMemory.IsPlan && GalaxyEditIsCompleted,
            isAbandoned: SelectedGalaxyMemory.IsPlan && GalaxyEditIsAbandoned,
            lifecycle: SelectedGalaxyMemory.Lifecycle,
            aiWeightProfile: SelectedGalaxyMemory.AiWeightProfile,
            reviewStatus: SelectedGalaxyMemory.ReviewStatus,
            constellationName: GalaxyEditGroup.Trim());

        StatusText = T("Main_GalaxyTaskSaved");
        await RefreshContextMemoriesAsync(updated.Id);
        if (updated.IsPlan && (updated.IsCompleted || updated.IsAbandoned))
        {
            EndFocusModeIfMatches(updated.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditGalaxyTask))]
    private async Task ConfirmSelectedMemory()
    {
        if (SelectedGalaxyMemory == null)
        {
            return;
        }

        if (SelectedGalaxyMemory.IsPendingReview)
        {
            await SaveGalaxyTask();
            var confirmed = await _dbService.ConfirmPendingContextMemoryAsync(SelectedGalaxyMemory.Id);
            StatusText = confirmed == null ? "无法确认这条记忆。" : $"已确认记忆：{confirmed.Title}";
            await RefreshContextMemoriesAsync(confirmed?.Id);
            IsGalaxyVisible = true;
            return;
        }

        StatusText = "这条记忆已经在星图里。";
    }

    [RelayCommand(CanExecute = nameof(CanEditGalaxyTask))]
    private async Task RejectSelectedMemory()
    {
        if (SelectedGalaxyMemory == null)
        {
            return;
        }

        await _dbService.RejectPendingContextMemoryAsync(SelectedGalaxyMemory.Id);
        StatusText = $"已忽略候选记忆：{SelectedGalaxyMemory.Title}";
        SelectedGalaxyMemory = null;
        await RefreshContextMemoriesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEditGalaxyTask))]
    private async Task DeleteGalaxyTask()
    {
        if (SelectedGalaxyMemory == null)
        {
            return;
        }

        var result = MessageBox.Show(
            string.Format(T("Main_GalaxyTaskDeleteConfirmFormat"), SelectedGalaxyMemory.Title),
            T("Main_GalaxyTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var deleted = await _dbService.DeleteContextMemoryAsync(SelectedGalaxyMemory.Id);
        if (!deleted)
        {
            StatusText = T("Main_GalaxyTaskDeleteFailed");
            return;
        }

        SelectedGalaxyMemory = null;
        StatusText = T("Main_GalaxyTaskDeleted");
        await RefreshContextMemoriesAsync();
    }

    public async Task MoveGalaxyTaskAsync(ContextMemory memory, double x, double y)
    {
        await _dbService.UpdateContextMemoryPositionAsync(memory.Id, x, y);
        await RefreshContextMemoriesAsync(memory.Id);
        StatusText = T("Main_GalaxyTaskMoved");
    }

    public void PreviewGalaxyTaskPosition(ContextMemory memory, double x, double y)
    {
        var current = ContextMemories.FirstOrDefault(item => item.Id == memory.Id);
        if (current == null)
        {
            return;
        }

        current.X = x;
        current.Y = y;
        if (ContextMemories.Count <= MaxInteractiveLinkRefreshNodes)
        {
            GalaxyLinks = new ObservableCollection<GalaxyLinkViewModel>(CreateMemoryGalaxyLinks(ContextMemories));
        }
    }

    private bool CanEditGalaxyTask()
    {
        return SelectedGalaxyMemory != null;
    }

    private bool CanSendConversationMessage()
    {
        return !string.IsNullOrWhiteSpace(ConversationInput);
    }

    [RelayCommand]
    private void DeleteConversationMessage(ConversationMessage? message)
    {
        if (message == null)
        {
            return;
        }

        message.StopStreaming();
        ConversationMessages.Remove(message);
        HasConversationStarted = ConversationMessages.Count > 0;
    }

    [RelayCommand]
    private void RewindConversationTo(ConversationMessage? message)
    {
        if (message == null)
        {
            return;
        }

        var index = ConversationMessages.IndexOf(message);
        if (index < 0)
        {
            return;
        }

        StopAssistantOutput();
        if (message.IsUser)
        {
            ConversationInput = message.Text;
            while (ConversationMessages.Count > index)
            {
                ConversationMessages[^1].StopStreaming();
                ConversationMessages.RemoveAt(ConversationMessages.Count - 1);
            }
        }
        else
        {
            while (ConversationMessages.Count > index + 1)
            {
                ConversationMessages[^1].StopStreaming();
                ConversationMessages.RemoveAt(ConversationMessages.Count - 1);
            }
        }

        HasConversationStarted = ConversationMessages.Count > 0;
        StatusText = T("Main_ConversationRewound");
    }

    [RelayCommand]
    private void StopAssistantOutput()
    {
        _assistantResponseCancellation?.Cancel();
        foreach (var message in ConversationMessages)
        {
            message.StopStreaming();
        }

        StopAssistantThinking();
        StatusText = T("Main_AssistantStopped");
    }

    private async Task DispatchConversationAsync(string text)
    {
        if (await TryHandleLocalCommandAsync(text))
        {
            return;
        }

        await DispatchContextAssistantAsync(text);
    }

    private async Task<bool> TryHandleLocalCommandAsync(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();

        if (await TryHandleDesktopInsightCommandAsync(text, normalized))
        {
            return true;
        }

        if (ContainsAny(normalized, "help", "commands", "帮助", "怎么用"))
        {
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantHelp")));
            return true;
        }

        if (ContainsAny(normalized, "settings", "configure", "设置", "配置"))
        {
            OpenSettings();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantSettingsOpened")));
            return true;
        }

        if (ContainsAny(normalized, "refresh", "reload", "刷新", "更新"))
        {
            await RefreshAsync();
            await RefreshContextMemoriesAsync();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantRefreshed")));
            return true;
        }

        if (ContainsAny(normalized, "daily review", "recap", "日报", "每日总结", "总结今天", "今日复盘"))
        {
            await GenerateDailyReview();
            return true;
        }

        if (ContainsAny(normalized, "complete", "done", "finish task", "完成任务", "完成了", "已完成"))
        {
            await CompleteLatestTaskAsync();
            return true;
        }

        if (ContainsAny(normalized, "backup", "备份"))
        {
            await SaveBackup();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantBackupDone")));
            return true;
        }

        if (ContainsAny(normalized, "restore", "恢复"))
        {
            await RestoreBackup();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantRestoreDone")));
            return true;
        }

        if (ContainsAny(normalized, "clear memories", "clear memory", "清空记忆", "删除记忆"))
        {
            var result = MessageBox.Show(
                T("Main_ClearMemoryConfirm"),
                T("Main_WindowTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                await _dbService.ClearContextMemoriesAsync();
                await RefreshContextMemoriesAsync();
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantMemoryCleared")));
            }

            return true;
        }

        if (ContainsAny(normalized, "clear usage", "clear data", "清空记录", "清除记录"))
        {
            await ClearUsageData();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantDataActionDone")));
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleDesktopInsightCommandAsync(string text, string normalized)
    {
        if (ContainsAny(normalized, "刚才在干嘛", "刚刚在干嘛", "时间切片", "行为回放", "窗口回放", "replay", "what was i doing"))
        {
            await GenerateDesktopContextInsightAsync("replay", text, TimeSpan.FromHours(2), saveArchive: false);
            return true;
        }

        if (ContainsAny(normalized, "检查计划", "计划进度", "plan progress", "推断完成", "是否完成", "哪些计划"))
        {
            await GenerateDesktopContextInsightAsync("plan_progress", text, TimeSpan.FromHours(24), saveArchive: false);
            return true;
        }

        if (ContainsAny(normalized, "继续刚才", "回到现场", "恢复现场", "继续上次", "resume scene", "continue last"))
        {
            await GenerateDesktopContextInsightAsync("resume_scene", text, TimeSpan.FromHours(24), saveArchive: true);
            return true;
        }

        if (ContainsAny(normalized, "鱼骨归因", "进程归因", "任务归因", "fishbone", "行为归因"))
        {
            await GenerateDesktopContextInsightAsync("fishbone", text, TimeSpan.FromHours(24), saveArchive: true);
            return true;
        }

        if (ContainsAny(normalized, "压缩记忆", "压缩上下文", "本地摘要", "生成摘要", "memory digest", "context digest"))
        {
            await GenerateDesktopContextInsightAsync("digest", text, TimeSpan.FromHours(24), saveArchive: true);
            return true;
        }

        if (ContainsAny(normalized, "解释星图", "星图解释", "星座解释", "为什么归类", "galaxy explain"))
        {
            await GenerateDesktopContextInsightAsync("galaxy", text, TimeSpan.FromHours(24), saveArchive: false);
            return true;
        }

        return false;
    }

    private async Task GenerateDesktopContextInsightAsync(
        string mode,
        string userInput,
        TimeSpan lookback,
        bool saveArchive)
    {
        HasConversationStarted = true;
        StartAssistantThinking();
        _assistantResponseCancellation?.Cancel();
        using var responseCancellation = new CancellationTokenSource();
        _assistantResponseCancellation = responseCancellation;
        StatusText = T("Main_ContextThinking");
        try
        {
            var snapshot = _processMonitor.SampleForegroundWindowFocus();
            var sessions = await _dbService.GetApplicationUsageSessionsSinceAsync(DateTime.Now - lookback);
            var memories = await GetInsightMemoriesAsync(userInput, snapshot);
            var insight = await CreateDesktopInsightAsync(mode, userInput, memories, sessions, snapshot, responseCancellation.Token);

            string? archivePath = null;
            if (saveArchive || mode == "digest")
            {
                archivePath = await ArchiveLocalContextDigestAsync(mode, memories, sessions, insight);
                await RefreshContextMemoriesAsync();
            }

            var response = FormatDesktopInsight(insight);
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                response += "\n\n已写入本地摘要：" + archivePath;
            }

            ConversationMessages.Add(ConversationMessage.Assistant(response));
            StatusText = T("Main_ContextReady");
        }
        catch (OperationCanceledException)
        {
            StatusText = T("Main_AssistantStopped");
        }
        finally
        {
            StopAssistantThinking();
            if (_assistantResponseCancellation == responseCancellation)
            {
                _assistantResponseCancellation = null;
            }
        }
    }

    private async Task<IReadOnlyList<ContextMemory>> GetInsightMemoriesAsync(
        string query,
        ForegroundFocusSnapshot? snapshot)
    {
        var retrieved = await _contextRetrievalService.RetrieveAsync(
            query + " plan scene digest fishbone galaxy resume replay",
            snapshot,
            18);
        var all = await _dbService.GetContextMemoriesAsync();
        return retrieved
            .Concat(all.Where(memory =>
                memory.IsPlan ||
                memory.Source.Contains("scene", StringComparison.OrdinalIgnoreCase) ||
                memory.Source.Contains("archive", StringComparison.OrdinalIgnoreCase) ||
                memory.Source.Contains("fishbone", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
                .ThenByDescending(memory => memory.UpdatedAt)
                .Take(24))
            .DistinctBy(memory => memory.Id)
            .Take(36)
            .ToList();
    }

    private async Task<DesktopContextInsight> CreateDesktopInsightAsync(
        string mode,
        string userInput,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        ForegroundFocusSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        if (_focusClient?.IsConfigured == true)
        {
            try
            {
                var insight = await _focusClient.CreateDesktopContextInsightAsync(
                    mode,
                    userInput,
                    memories,
                    sessions,
                    snapshot,
                    cancellationToken);
                if (insight != null && !string.IsNullOrWhiteSpace(insight.Summary))
                {
                    return insight;
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        return CreateFallbackDesktopInsight(mode, memories, sessions, snapshot);
    }

    private async Task DispatchContextAssistantAsync(string text)
    {
        StartAssistantThinking();
        _assistantResponseCancellation?.Cancel();
        using var responseCancellation = new CancellationTokenSource();
        _assistantResponseCancellation = responseCancellation;
        StatusText = T("Main_ContextRetrieving");
        try
        {
            var snapshot = _processMonitor.SampleForegroundWindowFocus();
            ContextMemory? savedPlan = null;
            if (MemoryExtractionService.LooksLikePlanMemory(text))
            {
                savedPlan = await _memoryExtractionService.SaveExplicitMemoryAsync(text);
                if (savedPlan != null)
                {
                    await RefreshContextMemoriesAsync(savedPlan.Id);
                }
            }

            var memories = await _contextRetrievalService.RetrieveAsync(text, snapshot);
            if (memories.Count < 2)
            {
                memories = await TryExpandSparseContextAsync(text, snapshot, memories);
            }

            var contextPack = ContextRetrievalService.BuildContextPack(memories);

        if (LooksLikeExplicitMemory(text))
        {
            var saved = await _memoryExtractionService.SaveExplicitMemoryAsync(text);
            if (saved != null)
            {
                await RefreshContextMemoriesAsync(saved.Id);
                ConversationMessages.Add(ConversationMessage.Assistant(
                    string.Format(T("Main_AssistantMemorySaved"), saved.Title)));
                StatusText = T("Main_MemorySaved");
                return;
            }
        }

        PersonalizedReplyResult? reply = null;
        if (_focusClient?.IsConfigured == true)
        {
            try
            {
                StatusText = T("Main_ContextThinking");
                reply = await _focusClient.CreatePersonalizedReplyAsync(text, contextPack, snapshot, responseCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText = T("Main_AssistantStopped");
                return;
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
                StatusText = ex.Message;
            }
        }

        if (reply != null && reply.UsedMemoryIds.Count > 0)
        {
            await _dbService.MarkContextMemoriesUsedAsync(reply.UsedMemoryIds);
        }

        if (reply?.SuggestedMemory is { ShouldRemember: true, Confidence: >= 0.72 } candidate &&
            _settingsService.Settings.AutoSaveMemories &&
            !string.IsNullOrWhiteSpace(candidate.Content) &&
            savedPlan == null)
        {
            var pending = await _memoryExtractionService.SaveCandidateForReviewAsync(candidate);
            await RefreshContextMemoriesAsync(pending?.Id);
            StatusText = pending == null
                ? StatusText
                : $"发现一条候选记忆，已放到待确认：{pending.Title}";
        }
        else if (savedPlan == null)
        {
            var autoMemory = await _memoryExtractionService.TryExtractAndSaveAsync(
                text,
                memories,
                _settingsService.Settings.AutoSaveMemories);
            if (autoMemory != null)
            {
                await RefreshContextMemoriesAsync(autoMemory.Id);
            }
        }

        ConversationMessages.Add(ConversationMessage.Assistant(
            !string.IsNullOrWhiteSpace(reply?.Reply)
                ? reply.Reply.Trim()
                : CreateLocalContextReply(memories, snapshot)));
            StatusText = T("Main_ContextReady");
        }
        finally
        {
            StopAssistantThinking();
            if (_assistantResponseCancellation == responseCancellation)
            {
                _assistantResponseCancellation = null;
            }
        }
    }

    private async Task<IReadOnlyList<ContextMemory>> TryExpandSparseContextAsync(
        string text,
        ForegroundFocusSnapshot? snapshot,
        IReadOnlyList<ContextMemory> currentMemories)
    {
        if (_focusClient?.IsConfigured != true)
        {
            return currentMemories;
        }

        try
        {
            StatusText = T("Main_ContextThinking");
            var allMemories = await _dbService.GetContextMemoriesAsync();
            var sessions = await _dbService.GetApplicationUsageSessionsSinceAsync(DateTime.Now.AddHours(-24));
            var digest = await _focusClient.CreateLocalMemoryDigestAsync(text, allMemories, sessions);
            if (digest is not { ShouldRemember: true, Confidence: >= 0.65 } ||
                string.IsNullOrWhiteSpace(digest.Content))
            {
                return currentMemories;
            }

            var saved = await _dbService.UpsertContextMemoryAsync(
                MemoryExtractionService.NormalizeCandidateTitle(digest.Title, digest.Content),
                digest.Content,
                MemoryExtractionService.ParseType(digest.Type),
                "ai-compression",
                string.Join(", ", digest.Tags.DefaultIfEmpty("digest").Take(8)),
                Math.Clamp(digest.Weight, 0.45, 0.9),
                memoryAxis: digest.MemoryAxis,
                aiDescription: digest.Description,
                aiExplanation: digest.Explanation,
                nextPrediction: digest.NextPrediction,
                isPlan: digest.IsPlan,
                isCompleted: digest.IsCompleted,
                aiWeightProfile: digest.WeightProfile);

            await RefreshContextMemoriesAsync(saved.Id);
            return await _contextRetrievalService.RetrieveAsync(text, snapshot, 10);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            return currentMemories;
        }
    }

    private DesktopContextInsight CreateFallbackDesktopInsight(
        string mode,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        ForegroundFocusSnapshot? snapshot)
    {
        var orderedSessions = sessions
            .OrderBy(session => session.StartTime)
            .ToList();
        var openPlans = memories
            .Where(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .OrderByDescending(memory => memory.Weight)
            .ThenByDescending(memory => memory.UpdatedAt)
            .ToList();
        var completedPlans = memories
            .Where(memory => memory.IsPlan && memory.IsCompleted)
            .OrderByDescending(memory => memory.CompletedAt ?? memory.UpdatedAt)
            .Take(5)
            .ToList();

        var summary = mode switch
        {
            "replay" => CreateReplaySummary(orderedSessions, snapshot),
            "plan_progress" => $"当前有 {openPlans.Count} 个未完成 plan，{completedPlans.Count} 个最近完成的 plan。下面是基于窗口行为的弱证据推断。",
            "resume_scene" => CreateResumeSceneSummary(orderedSessions, openPlans, snapshot),
            "fishbone" => "已把最近进程行为按 plan/tag 做了一次本地鱼骨归因。",
            "galaxy" => "已根据星座、tag、plan 完成状态解释当前记忆星图。",
            _ => "已生成一份本地桌面上下文摘要。"
        };

        return new DesktopContextInsight
        {
            Summary = summary,
            Evidence = CreateSessionEvidence(orderedSessions, snapshot),
            PlanSuggestions = CreatePlanProgressSuggestions(openPlans, orderedSessions),
            Fishbone = CreateFishboneLines(openPlans, orderedSessions),
            ConstellationExplanations = CreateConstellationExplanationLines(memories),
            SuggestedNextAction = CreateSuggestedNextAction(openPlans, orderedSessions, snapshot)
        };
    }

    private async Task<string?> ArchiveLocalContextDigestAsync(
        string reason,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        DesktopContextInsight insight)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perelegans",
            "memories",
            "digest");
        Directory.CreateDirectory(directory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var token = CreateFileToken(reason);
        var jsonPath = Path.Combine(directory, $"{stamp}.{token}.json");
        var mdPath = Path.Combine(directory, $"{stamp}.{token}.md");
        var markdownDigest = BuildMarkdownDigest(reason, insight, memories, sessions);
        var isDailyReview = reason == "daily";

        var archive = new
        {
            createdAt = DateTime.Now,
            reason,
            insight,
            memories = memories
                .OrderByDescending(memory => memory.UpdatedAt)
                .Take(40)
                .Select(memory => new
                {
                    memory.Id,
                    memory.Title,
                    Type = memory.Type.ToString(),
                    memory.MemoryAxis,
                    memory.Tags,
                    memory.ConstellationName,
                    memory.IsPlan,
                    memory.IsCompleted,
                    memory.IsAbandoned,
                    Lifecycle = memory.Lifecycle.ToString(),
                    memory.Weight,
                    memory.Content,
                    memory.AiDescription,
                    memory.AiExplanation,
                    memory.NextPrediction
                }),
            sessions = sessions
                .OrderBy(session => session.StartTime)
                .TakeLast(120)
                .Select(session => new
                {
                    session.ProcessName,
                    session.ExecutablePath,
                    session.StartTime,
                    session.EndTime,
                    minutes = Math.Round(session.Duration.TotalMinutes, 1),
                    session.IsKnownProductivityApp
                })
        };

        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(archive, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        await File.WriteAllTextAsync(mdPath, markdownDigest, Encoding.UTF8);

        await _dbService.UpsertContextMemoryAsync(
            isDailyReview ? $"今日复盘 {DateTime.Now:MM-dd HH:mm}" : $"本地上下文摘要 {DateTime.Now:MM-dd HH:mm}",
            markdownDigest,
            isDailyReview ? ContextMemoryType.Review : ContextMemoryType.Event,
            reason == "fishbone" ? "ai-fishbone" : isDailyReview ? "daily-review" : "local-archive",
            reason == "fishbone" ? "digest, rag, fishbone" : isDailyReview ? "review, daily, recap, rag, scene" : "digest, rag, scene",
            0.74,
            memoryAxis: isDailyReview ? "review" : "event",
            aiDescription: insight.Summary,
            aiExplanation: "这是一份从本地记忆、plan 状态和 Win32 进程切换行为压缩出的上下文摘要，用于后续 RAG 恢复现场。",
            nextPrediction: insight.SuggestedNextAction,
            isPlan: false,
            suppressPlanDetection: isDailyReview);

        return mdPath;
    }

    private static string FormatDesktopInsight(DesktopContextInsight insight)
    {
        var builder = new StringBuilder();
        builder.AppendLine(insight.Summary.Trim());
        AppendSection(builder, "证据", insight.Evidence);
        AppendSection(builder, "计划推断", insight.PlanSuggestions);
        AppendSection(builder, "鱼骨归因", insight.Fishbone);
        AppendSection(builder, "星图解释", insight.ConstellationExplanations);

        if (!string.IsNullOrWhiteSpace(insight.SuggestedNextAction))
        {
            builder.AppendLine();
            builder.AppendLine("下一步");
            builder.AppendLine(insight.SuggestedNextAction.Trim());
        }

        return builder.ToString().Trim();
    }

    private static string BuildMarkdownDigest(
        string reason,
        DesktopContextInsight insight,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Perelegans 本地上下文摘要");
        builder.AppendLine();
        builder.AppendLine($"- reason: {reason}");
        builder.AppendLine($"- created_at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("## 主轴");
        builder.AppendLine(insight.Summary.Trim());
        AppendMarkdownList(builder, "## 证据", insight.Evidence);
        AppendMarkdownList(builder, "## Plan 推断", insight.PlanSuggestions);
        AppendMarkdownList(builder, "## 鱼骨归因", insight.Fishbone);
        AppendMarkdownList(builder, "## 星图解释", insight.ConstellationExplanations);
        builder.AppendLine("## 下一步预测");
        builder.AppendLine(string.IsNullOrWhiteSpace(insight.SuggestedNextAction)
            ? "暂无明确下一步。"
            : insight.SuggestedNextAction.Trim());
        builder.AppendLine();
        builder.AppendLine("## 相关记忆");
        foreach (var memory in memories.OrderByDescending(memory => memory.UpdatedAt).Take(20))
        {
            builder.AppendLine($"- [{memory.Id}] {memory.Title} | {memory.ConstellationName} | {memory.Tags} | plan:{memory.IsPlan}/{memory.IsCompleted}");
        }

        builder.AppendLine();
        builder.AppendLine("## 进程切片");
        foreach (var session in sessions.OrderBy(session => session.StartTime).TakeLast(40))
        {
            builder.AppendLine($"- {session.StartTime:HH:mm}-{session.EndTime:HH:mm} {session.ProcessName} {Math.Max(1, (int)Math.Round(session.Duration.TotalMinutes))}m");
        }

        return builder.ToString();
    }

    private void QueueSceneCheckpoint(ForegroundFocusSnapshot snapshot)
    {
        if (snapshot.Duration < TimeSpan.FromMinutes(12) ||
            DateTime.Now - _lastSceneCheckpointAt < TimeSpan.FromMinutes(30) ||
            string.Equals(_lastSceneCheckpointProcess, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastSceneCheckpointAt = DateTime.Now;
        _lastSceneCheckpointProcess = snapshot.ProcessName;
        var snapshotCopy = snapshot;
        _ = Task.Run(async () =>
        {
            try
            {
                var memories = await _dbService.GetContextMemoriesAsync();
                var openPlan = memories
                    .Where(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
                    .OrderByDescending(memory => CalculateProcessPlanAffinity(snapshotCopy.ProcessName, memory))
                    .FirstOrDefault();
                if (openPlan == null)
                {
                    return;
                }

                var related = CalculateProcessPlanAffinity(snapshotCopy.ProcessName, openPlan) > 0
                    ? $"可能关联未完成 plan「{openPlan.Title}」。"
                    : "暂未找到明确关联的 plan，但这是一个可恢复的工作现场。";
                await _dbService.UpsertContextMemoryAsync(
                    $"现场：{snapshotCopy.ProcessName}",
                    $"{DateTime.Now:HH:mm} 左右，前台在 {snapshotCopy.ProcessName} 停留约 {Math.Max(1, (int)Math.Round(snapshotCopy.Duration.TotalMinutes))} 分钟。{related}",
                    ContextMemoryType.Event,
                    "scene-checkpoint",
                    "scene, checkpoint",
                    0.42,
                    memoryAxis: "event",
                    aiDescription: $"系统自动保留的桌面现场：{snapshotCopy.ProcessName}",
                    aiExplanation: "当用户稍后说“继续刚才”时，这条记忆可帮助恢复最近工作现场。",
                    nextPrediction: openPlan == null ? string.Empty : openPlan.NextPrediction);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        });
    }

    private async Task<TaskInstructionResult?> TryParseWithAiAsync(string text)
    {
        if (_focusClient?.IsConfigured != true)
        {
            return null;
        }

        StatusText = T("Main_ParsingInput");
        try
        {
            return await _focusClient.ParseTaskInstructionAsync(text, CurrentFocusGoal);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            StatusText = ex.Message;
            return null;
        }
    }

    private async Task DispatchParsedInstructionAsync(TaskInstructionResult instruction)
    {
        var command = NormalizeCommand(instruction.Command);
        var tasks = NormalizeTasks(instruction.Tasks, instruction.PrimaryTask);
        var intent = instruction.Intent.Trim().ToLowerInvariant();

        if (command != "none")
        {
            await ExecuteCommandAsync(command);
        }

        if ((intent == "task" || intent == "mixed") && tasks.Count > 0)
        {
            await CreateAdventureTasksAsync(tasks, string.Join("；", tasks));
            return;
        }

        if (command != "none")
        {
            return;
        }

        ConversationMessages.Add(ConversationMessage.Assistant(
            string.IsNullOrWhiteSpace(instruction.AssistantMessage)
                ? T("Main_AssistantNoTaskRecognized")
                : instruction.AssistantMessage.Trim()));
    }

    private async Task DispatchLocalFallbackAsync(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();

        if (ContainsAny(normalized, "help", "commands", "怎么", "帮助"))
        {
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantHelp")));
            return;
        }

        if (ContainsAny(normalized, "settings", "configure", "设置", "配置"))
        {
            OpenSettings();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantSettingsOpened")));
            return;
        }

        if (ContainsAny(normalized, "refresh", "reload", "刷新", "更新"))
        {
            await RefreshAsync();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantRefreshed")));
            return;
        }

        if (ContainsAny(normalized, "review", "daily", "recap", "复盘", "日报", "总结今天"))
        {
            await GenerateDailyReview();
            return;
        }

        if (ContainsAny(normalized, "pause", "stop monitor", "disable monitor", "停止监控", "暂停监控"))
        {
            SetMonitorEnabled(false);
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantMonitoringPaused")));
            return;
        }

        if (ContainsAny(normalized, "start", "resume", "enable monitor", "开始监控", "启动监控", "继续监控"))
        {
            SetMonitorEnabled(true);
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantMonitoringOn")));
            return;
        }

        if (ContainsAny(normalized, "backup", "备份"))
        {
            await SaveBackup();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantBackupDone")));
            return;
        }

        if (ContainsAny(normalized, "restore", "恢复"))
        {
            await RestoreBackup();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantRestoreDone")));
            return;
        }

        if (ContainsAny(normalized, "clear usage", "clear data", "清空", "清除"))
        {
            await ClearUsageData();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantDataActionDone")));
            return;
        }

        if (ContainsAny(normalized, "complete", "done", "finish task", "完成任务", "完成了", "已完成"))
        {
            await CompleteLatestTaskAsync();
            return;
        }

        if (!LooksLikeTask(text))
        {
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantNoTaskRecognized")));
            return;
        }

        await CreateAdventureTasksAsync([text], text);
    }

    private async Task ExecuteCommandAsync(string command)
    {
        switch (command)
        {
            case "settings":
                OpenSettings();
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantSettingsOpened")));
                break;
            case "refresh":
                await RefreshAsync();
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantRefreshed")));
                break;
            case "daily_review":
                await GenerateDailyReview();
                break;
            case "pause_monitor":
                SetMonitorEnabled(false);
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantMonitoringPaused")));
                break;
            case "start_monitor":
                SetMonitorEnabled(true);
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantMonitoringOn")));
                break;
            case "complete_task":
                await CompleteLatestTaskAsync();
                break;
            case "backup":
                await SaveBackup();
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantBackupDone")));
                break;
            case "restore":
                await RestoreBackup();
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantRestoreDone")));
                break;
            case "clear_data":
                await ClearUsageData();
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantDataActionDone")));
                break;
        }
    }

    private static string NormalizeCommand(string? command)
    {
        return command?.Trim().ToLowerInvariant() switch
        {
            "start" or "start_monitor" or "resume" => "start_monitor",
            "pause" or "stop" or "pause_monitor" or "stop_monitor" => "pause_monitor",
            "complete" or "complete_task" or "done" or "finish" => "complete_task",
            "reload" or "refresh" => "refresh",
            "review" or "daily_review" or "recap" => "daily_review",
            "config" or "configure" or "settings" => "settings",
            "backup" => "backup",
            "restore" => "restore",
            "clear" or "clear_data" or "clear_usage" => "clear_data",
            _ => "none"
        };
    }

    private static List<string> NormalizeTasks(IEnumerable<string>? tasks, string? primaryTask)
    {
        var result = (tasks ?? [])
            .Select(task => task.Trim())
            .Where(task => !string.IsNullOrWhiteSpace(task))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(primaryTask))
        {
            result.Add(primaryTask.Trim());
        }

        return result;
    }

    private static bool LooksLikeTask(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length < 3)
        {
            return false;
        }

        var lower = normalized.ToLowerInvariant();
        if (normalized.EndsWith('?') ||
            normalized.EndsWith('？') ||
            ContainsAny(lower, "是什么", "为什么", "怎么", "如何", "吗", "呢", "hello", "hi", "thanks", "谢谢"))
        {
            return false;
        }

        return ContainsAny(lower,
            "写", "做", "完成", "复习", "学习", "整理", "实现", "开发", "阅读", "总结", "修改", "准备",
            "write", "finish", "study", "review", "implement", "build", "read", "prepare", "draft", "fix");
    }

    private async Task CreateAdventureTasksAsync(IReadOnlyList<string> tasks, string originalInput)
    {
        var created = new List<FocusTask>();
        foreach (var task in tasks)
        {
            var adventure = await CreateAdventureDraftAsync(task);
            var focusTask = await _dbService.CreateFocusTaskAsync(task, originalInput, adventure);
            created.Add(focusTask);
            ConversationMessages.Add(ConversationMessage.Assistant(focusTask.QuestNarrative));
        }

        if (created.Count == 0)
        {
            return;
        }

        SetFocusGoal(string.Join("；", created.Select(t => t.Title)));
        await RefreshFocusTasksAsync();
        IsGalaxyVisible = true;
    }

    private async Task<TaskAdventureDraft?> CreateAdventureDraftAsync(string task)
    {
        if (_focusClient?.IsConfigured == true)
        {
            try
            {
                var draft = await _focusClient.CreateTaskAdventureAsync(task);
                if (draft != null)
                {
                    return draft;
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        return new TaskAdventureDraft
        {
            QuestTitle = task.Length <= 12 ? task : task[..12],
            QuestNarrative = string.Format(T("Main_FallbackQuestNarrativeFormat"), task),
            RewardName = T("Main_FallbackRewardName"),
            Summary = string.Format(T("Main_FallbackTaskSummaryFormat"), task),
            NextAction = string.Format(T("Main_FallbackNextActionFormat"), task),
            Difficulty = 2,
            EstimatedMinutes = 25,
            Tags = ["focus", "task"],
            ConstellationName = T("Main_FallbackConstellationName")
        };
    }

    private async Task CompleteLatestTaskAsync()
    {
        if (_focusModeService.IsActive && _focusModeService.TaskMemoryId.HasValue)
        {
            var completedMemory = await _dbService.CompleteContextMemoryAsync(_focusModeService.TaskMemoryId.Value);
            if (completedMemory != null)
            {
                ConversationMessages.Add(ConversationMessage.Assistant(
                    string.Format(T("Main_FocusModeCompletedFormat"), completedMemory.Title)));
                await RefreshContextMemoriesAsync(completedMemory.Id);
                EndFocusModeIfMatches(completedMemory.Id);
                IsGalaxyVisible = true;
                return;
            }
        }

        var latest = FocusTasks
            .Where(t => t.Status == FocusTaskStatus.Active)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefault();
        if (latest == null)
        {
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantNoActiveTask")));
            return;
        }

        TaskCompletionDraft? completion = null;
        if (_focusClient?.IsConfigured == true)
        {
            try
            {
                completion = await _focusClient.CreateTaskCompletionAsync(latest.Title, latest.RewardName);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        completion ??= new TaskCompletionDraft
        {
            CompletionNarrative = string.Format(T("Main_FallbackCompletionNarrativeFormat"), latest.Title, latest.RewardName),
            RewardName = latest.RewardName
        };

        var completed = await _dbService.CompleteLatestActiveFocusTaskAsync(completion);
        if (completed == null)
        {
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantNoActiveTask")));
            return;
        }

        ConversationMessages.Add(ConversationMessage.Assistant(completed.CompletionNarrative));
        await RefreshFocusTasksAsync(completed.Id);
        IsGalaxyVisible = true;
    }

    private async Task RefreshFocusTasksAsync(int? selectedTaskId = null)
    {
        selectedTaskId ??= null;
        var tasks = await _dbService.GetFocusTasksAsync();
        var links = await _dbService.GetFocusTaskLinksAsync();
        FocusTasks = new ObservableCollection<FocusTask>(tasks.OrderBy(t => t.CreatedAt));
        GalaxyLinks = new ObservableCollection<GalaxyLinkViewModel>(CreateGalaxyLinks(tasks, links));
        OnPropertyChanged(nameof(FocusTaskCountText));
    }

    private async Task RefreshContextMemoriesAsync(int? highlightId = null)
    {
        await _dbService.RefreshContextMemoryWeightsAsync();
        var visibleMemories = (await _dbService.GetContextMemoriesAsync())
            .Where(memory => !IsSceneCheckpointMemory(memory))
            .ToList();
        var pendingMemories = (await _dbService.GetPendingContextMemoriesAsync())
            .Where(memory => !IsSceneCheckpointMemory(memory))
            .ToList();
        _allVisibleContextMemories = visibleMemories;
        _memorySearchIndex = visibleMemories.ToDictionary(memory => memory.Id, CreateMemorySearchText);
        PendingContextMemories = new ObservableCollection<ContextMemory>(pendingMemories);
        AvailableGalaxyTags = new ObservableCollection<string>(CreateAvailableMemoryTags(visibleMemories));
        AvailableGalaxyGroups = new ObservableCollection<string>(CreateAvailableMemoryGroups(visibleMemories));
        ResetMissingExpandedMemoryConstellation(visibleMemories);
        ApplyGalaxyMemoryFilters();
        SelectedGalaxyMemory = highlightId.HasValue
            ? ContextMemories.Concat(PendingContextMemories).FirstOrDefault(memory => memory.Id == highlightId.Value)
            : SelectedGalaxyMemory == null
                ? null
                : ContextMemories.Concat(PendingContextMemories).FirstOrDefault(memory => memory.Id == SelectedGalaxyMemory.Id);
        OnPropertyChanged(nameof(MemoryCountText));
        OnPropertyChanged(nameof(PendingMemoryCountText));
        OnPropertyChanged(nameof(HasPendingMemories));
        OnPropertyChanged(nameof(HasGalaxyFilters));

        if (highlightId.HasValue)
        {
            StatusText = string.Format(T("Main_MemorySaved"), highlightId.Value);
        }

        OnPropertyChanged(nameof(CurrentFocusGoalDisplay));
        OnPropertyChanged(nameof(FocusModeStatusText));
    }

    private void ResetMissingExpandedMemoryConstellation(IReadOnlyList<ContextMemory> visibleMemories)
    {
        if (string.IsNullOrWhiteSpace(_expandedMemoryConstellation) ||
            visibleMemories.Any(memory => string.Equals(
                memory.ConstellationName.Trim(),
                _expandedMemoryConstellation,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var staleConstellation = _expandedMemoryConstellation;
        _expandedMemoryConstellation = string.Empty;
        if (string.Equals(
                NormalizeGalaxyFilter(GalaxyGroupFilter, "group"),
                staleConstellation,
                StringComparison.OrdinalIgnoreCase))
        {
            GalaxyGroupFilter = string.Empty;
        }
    }

    private void ApplyGalaxyMemoryFilters()
    {
        var search = NormalizeGalaxyFilter(GalaxySearchText, "search");
        var tagFilter = NormalizeGalaxyFilter(GalaxyTagFilter, "tag");
        var groupFilter = NormalizeGalaxyFilter(GalaxyGroupFilter, "group");
        var hasFilters = !string.IsNullOrWhiteSpace(search) ||
            !string.IsNullOrWhiteSpace(tagFilter) ||
            !string.IsNullOrWhiteSpace(groupFilter);
        var isExpandedConstellation = !string.IsNullOrWhiteSpace(_expandedMemoryConstellation) &&
            string.IsNullOrWhiteSpace(search) &&
            string.IsNullOrWhiteSpace(tagFilter) &&
            string.Equals(groupFilter, _expandedMemoryConstellation, StringComparison.OrdinalIgnoreCase);
        var filtered = isExpandedConstellation
            ? CreateExpandedConstellationMemorySlice(_allVisibleContextMemories, _expandedMemoryConstellation)
            : _allVisibleContextMemories
                .Where(memory => MatchesGalaxyFilters(memory, search, tagFilter, groupFilter))
                .ToList();
        var constellationNodes = hasFilters
            ? []
            : CreateMemoryConstellationNodes(filtered).ToList();
        MemoryConstellations = new ObservableCollection<MemoryConstellationNodeViewModel>(constellationNodes);
        if (!hasFilters)
        {
            ContextMemories = [];
            GalaxyLinks = new ObservableCollection<GalaxyLinkViewModel>(
                CreateMemoryConstellationLinks(filtered, constellationNodes));
            FishboneBranches = new ObservableCollection<FishboneBranchViewModel>(CreateFishboneBranches(filtered));
            OnPropertyChanged(nameof(MemoryCountText));
            OnPropertyChanged(nameof(IsMemoryConstellationLayer));
            OnPropertyChanged(nameof(IsMemoryNodeLayer));
            OnPropertyChanged(nameof(IsGalaxyEmpty));
            return;
        }

        var displayed = CreateDisplayedMemorySlice(
            filtered,
            hasFilters,
            SelectedGalaxyMemory?.Id);
        ContextMemories = new ObservableCollection<ContextMemory>(displayed);
        GalaxyLinks = new ObservableCollection<GalaxyLinkViewModel>(CreateMemoryGalaxyLinks(displayed));
        FishboneBranches = new ObservableCollection<FishboneBranchViewModel>(CreateFishboneBranches(displayed));
        OnPropertyChanged(nameof(MemoryCountText));
        OnPropertyChanged(nameof(IsMemoryConstellationLayer));
        OnPropertyChanged(nameof(IsMemoryNodeLayer));
        OnPropertyChanged(nameof(IsGalaxyEmpty));
    }

    private bool MatchesGalaxyFilters(ContextMemory memory, string search, string tagFilter, string groupFilter)
    {
        if (!string.IsNullOrWhiteSpace(search) &&
            !ContainsAllSearchTerms(memory, search))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tagFilter) &&
            !SplitTagsForInsight(memory.Tags)
                .Any(tag => string.Equals(tag, tagFilter, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(groupFilter) &&
            !string.Equals(memory.ConstellationName.Trim(), groupFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<ContextMemory> CreateDisplayedMemorySlice(
        IReadOnlyList<ContextMemory> memories,
        bool hasFilters,
        int? pinnedMemoryId)
    {
        var limit = hasFilters ? MaxFilteredMemoryNodes : MaxDefaultMemoryNodes;
        if (memories.Count <= limit)
        {
            return memories;
        }

        var displayed = memories
            .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .ThenByDescending(memory => memory.Weight)
            .ThenByDescending(memory => memory.UpdatedAt)
            .Take(limit)
            .ToList();

        if (pinnedMemoryId.HasValue &&
            displayed.All(memory => memory.Id != pinnedMemoryId.Value))
        {
            var pinned = memories.FirstOrDefault(memory => memory.Id == pinnedMemoryId.Value);
            if (pinned != null)
            {
                displayed[^1] = pinned;
            }
        }

        return displayed;
    }

    private static string NormalizeGalaxyFilter(string? value, string kind)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var lower = normalized.ToLowerInvariant();
        var isPlaceholder = kind switch
        {
            "tag" => lower is "tag" or "tags" or "\u6807\u7b7e",
            "group" => lower is "group" or "groups" or "\u5206\u7ec4",
            "search" => lower is "search" or "\u641c\u7d22" or "\u641c\u7d22\u6807\u9898\u3001\u5185\u5bb9\u3001\u6807\u7b7e",
            _ => false
        };

        return isPlaceholder ? string.Empty : normalized;
    }

    private bool ContainsAllSearchTerms(ContextMemory memory, string search)
    {
        var haystack = _memorySearchIndex.TryGetValue(memory.Id, out var indexedText)
            ? indexedText
            : CreateMemorySearchText(memory);
        return SplitTagsForInsight(search)
            .DefaultIfEmpty(search.Trim().ToLowerInvariant())
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateMemorySearchText(ContextMemory memory)
    {
        return string.Join(
            ' ',
            memory.Title,
            memory.Content,
            memory.Tags,
            memory.ConstellationName,
            memory.AiDescription,
            memory.AiExplanation,
            memory.NextPrediction).ToLowerInvariant();
    }

    private static IReadOnlyList<string> CreateAvailableMemoryTags(IEnumerable<ContextMemory> memories)
    {
        return memories
            .SelectMany(memory => SplitTagsForInsight(memory.Tags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> CreateAvailableMemoryGroups(IEnumerable<ContextMemory> memories)
    {
        return memories
            .Select(memory => memory.ConstellationName.Trim())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsSceneCheckpointMemory(ContextMemory memory)
    {
        return memory.Source.Contains("scene-checkpoint", StringComparison.OrdinalIgnoreCase) ||
               memory.Source.Contains("resume-scene", StringComparison.OrdinalIgnoreCase) ||
               memory.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Any(tag => string.Equals(tag, "scene", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(tag, "checkpoint", StringComparison.OrdinalIgnoreCase)) ||
               memory.Title.StartsWith("现场：", StringComparison.OrdinalIgnoreCase);
    }

    private DailyReviewDraft CreateFallbackDailyReview()
    {
        var todayTasks = FocusTasks
            .Where(task => task.CreatedAt.Date == DateTime.Today || task.CompletedAt?.Date == DateTime.Today)
            .ToList();
        var completed = todayTasks.Count(task => task.Status == FocusTaskStatus.Completed);
        var active = todayTasks.Count(task => task.Status == FocusTaskStatus.Active);

        return new DailyReviewDraft
        {
            Review = string.Format(T("Main_FallbackDailyReviewFormat"), completed, active),
            Encouragement = T("Main_DailyReviewFallbackEncouragement"),
            Highlights = todayTasks
                .Where(task => task.Status == FocusTaskStatus.Completed)
                .Select(task => task.Title)
                .Take(3)
                .DefaultIfEmpty(T("Main_DailyReviewNoCompletedTasks"))
                .ToList(),
            Risks = todayTasks
                .Where(task => task.Status == FocusTaskStatus.Active)
                .Select(task => string.Format(T("Main_DailyReviewActiveTaskFormat"), task.Title))
                .Take(3)
                .DefaultIfEmpty(T("Main_DailyReviewNoRisk"))
                .ToList(),
            SuggestedNextAction = T("Main_DailyReviewFallbackNextAction")
        };
    }

    private static string FormatDailyReview(DailyReviewDraft review)
    {
        var encouragement = string.IsNullOrWhiteSpace(review.Encouragement)
            ? string.Empty
            : T("Main_DailyReviewEncouragement") + "\n" + review.Encouragement.Trim() + "\n\n";
        var highlights = review.Highlights.Count == 0
            ? string.Empty
            : "\n" + T("Main_DailyReviewHighlights") + "\n" + string.Join("\n", review.Highlights.Select(item => $"• {item}"));
        var risks = review.Risks.Count == 0
            ? string.Empty
            : "\n" + T("Main_DailyReviewRisks") + "\n" + string.Join("\n", review.Risks.Select(item => $"• {item}"));
        var next = string.IsNullOrWhiteSpace(review.SuggestedNextAction)
            ? string.Empty
            : "\n" + T("Main_DailyReviewNextAction") + "\n" + review.SuggestedNextAction.Trim();

        return $"{encouragement}{T("Main_DailyReviewOverview")}\n{review.Review.Trim()}{highlights}{risks}{next}";
    }

    private static string CreateReplaySummary(
        IReadOnlyList<ApplicationUsageSession> sessions,
        ForegroundFocusSnapshot? snapshot)
    {
        if (sessions.Count == 0)
        {
            return snapshot == null
                ? "还没有足够的窗口切换记录可回放。"
                : $"当前现场停在 {snapshot.ProcessName}，已持续约 {Math.Max(1, (int)Math.Round(snapshot.Duration.TotalMinutes))} 分钟。";
        }

        var first = sessions.First();
        var last = sessions.Last();
        var top = sessions
            .GroupBy(session => session.ProcessName)
            .OrderByDescending(group => group.Sum(session => session.Duration.TotalMinutes))
            .Take(3)
            .Select(group => group.Key);
        return $"{first.StartTime:HH:mm} 到 {last.EndTime:HH:mm} 的主要现场是 {string.Join("、", top)}，中间发生了 {sessions.Count} 次可记录窗口停留。";
    }

    private static string CreateResumeSceneSummary(
        IReadOnlyList<ApplicationUsageSession> sessions,
        IReadOnlyList<ContextMemory> openPlans,
        ForegroundFocusSnapshot? snapshot)
    {
        var plan = openPlans.FirstOrDefault();
        var current = snapshot == null
            ? sessions.LastOrDefault()?.ProcessName
            : snapshot.ProcessName;
        if (plan == null)
        {
            return string.IsNullOrWhiteSpace(current)
                ? "目前没有足够的 plan 和窗口记录来恢复现场。"
                : $"最近现场停在 {current}，但没有匹配到明确的未完成 plan。";
        }

        return $"最值得恢复的现场是「{plan.Title}」。最近桌面信号指向 {current ?? "未知应用"}，可以从该 plan 的下一步继续。";
    }

    private static List<string> CreateSessionEvidence(
        IReadOnlyList<ApplicationUsageSession> sessions,
        ForegroundFocusSnapshot? snapshot)
    {
        var evidence = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Minutes = Math.Max(1, (int)Math.Round(group.Sum(session => session.Duration.TotalMinutes))),
                Switches = group.Count(),
                Last = group.Max(session => session.EndTime)
            })
            .OrderByDescending(item => item.Minutes)
            .Take(5)
            .Select(item => $"{item.ProcessName}: {item.Minutes} 分钟，{item.Switches} 段，最近 {item.Last:HH:mm}")
            .ToList();

        if (snapshot != null)
        {
            evidence.Insert(0, $"当前前台：{snapshot.ProcessName}，已持续约 {Math.Max(1, (int)Math.Round(snapshot.Duration.TotalMinutes))} 分钟");
        }

        if (evidence.Count == 0)
        {
            evidence.Add("暂无足够进程切换证据。");
        }

        return evidence;
    }

    private static List<string> CreatePlanProgressSuggestions(
        IReadOnlyList<ContextMemory> openPlans,
        IReadOnlyList<ApplicationUsageSession> sessions)
    {
        if (openPlans.Count == 0)
        {
            return ["没有未完成 plan 需要推断。"];
        }

        return openPlans
            .Take(6)
            .Select(plan =>
            {
                var matched = sessions
                    .Where(session => CalculateProcessPlanAffinity(session.ProcessName, plan) > 0)
                    .ToList();
                if (matched.Count == 0)
                {
                    return $"「{plan.Title}」仍是 open。最近进程证据不足，不建议自动勾选完成。";
                }

                var minutes = Math.Max(1, (int)Math.Round(matched.Sum(session => session.Duration.TotalMinutes)));
                var processes = string.Join("、", matched.Select(session => session.ProcessName).Distinct().Take(3));
                return $"「{plan.Title}」可能有推进：{processes} 共约 {minutes} 分钟。建议询问用户确认后再勾选完成。";
            })
            .ToList();
    }

    private static List<string> CreateFishboneLines(
        IReadOnlyList<ContextMemory> openPlans,
        IReadOnlyList<ApplicationUsageSession> sessions)
    {
        var lines = new List<string>();
        foreach (var plan in openPlans.Take(6))
        {
            var matched = sessions
                .Where(session => CalculateProcessPlanAffinity(session.ProcessName, plan) > 0)
                .GroupBy(session => session.ProcessName)
                .Select(group => $"{group.Key}:{Math.Max(1, (int)Math.Round(group.Sum(session => session.Duration.TotalMinutes)))}m")
                .Take(4)
                .ToList();
            lines.Add(matched.Count == 0
                ? $"主骨：{plan.Title} / 分支：暂无明确进程证据 / tag：{plan.Tags}"
                : $"主骨：{plan.Title} / 分支：{string.Join("、", matched)} / tag：{plan.Tags}");
        }

        if (lines.Count == 0)
        {
            lines.AddRange(sessions
                .GroupBy(session => session.ProcessName)
                .OrderByDescending(group => group.Sum(session => session.Duration.TotalMinutes))
                .Take(5)
                .Select(group => $"主骨：未归属行为 / 分支：{group.Key}:{Math.Max(1, (int)Math.Round(group.Sum(session => session.Duration.TotalMinutes)))}m"));
        }

        return lines.Count == 0 ? ["暂无可归因的进程行为。"] : lines;
    }

    private static List<string> CreateConstellationExplanationLines(IEnumerable<ContextMemory> memories)
    {
        return memories
            .Where(memory => !string.IsNullOrWhiteSpace(memory.ConstellationName))
            .GroupBy(memory => memory.ConstellationName)
            .OrderByDescending(group => group.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned))
            .ThenByDescending(group => group.Count())
            .Take(8)
            .Select(group =>
            {
                var open = group.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned);
                var completed = group.Count(memory => memory.IsPlan && memory.IsCompleted);
                var tags = string.Join(", ", group
                    .SelectMany(memory => SplitTagsForInsight(memory.Tags))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5));
                return $"{group.Key}: {group.Count()} 个节点，open plan {open} 个，done plan {completed} 个；主要 tag：{(string.IsNullOrWhiteSpace(tags) ? "无" : tags)}";
            })
            .DefaultIfEmpty("暂无可解释的星座聚类。")
            .ToList();
    }

    private static string CreateSuggestedNextAction(
        IReadOnlyList<ContextMemory> openPlans,
        IReadOnlyList<ApplicationUsageSession> sessions,
        ForegroundFocusSnapshot? snapshot)
    {
        var plan = openPlans.FirstOrDefault();
        if (plan != null && !string.IsNullOrWhiteSpace(plan.NextPrediction))
        {
            return plan.NextPrediction;
        }

        if (plan != null)
        {
            return $"先回到「{plan.Title}」，把它拆成一个 15 分钟内能完成的小动作。";
        }

        var lastProcess = snapshot?.ProcessName ?? sessions.LastOrDefault()?.ProcessName;
        return string.IsNullOrWhiteSpace(lastProcess)
            ? "先创建一个明确的 plan，让后续进程行为可以被归因。"
            : $"回到 {lastProcess}，确认刚才现场是否还需要保存成一个 plan。";
    }

    private static int CalculateProcessPlanAffinity(string processName, ContextMemory plan)
    {
        var processTags = InferProcessTags(processName);
        var planTags = SplitTagsForInsight(plan.Tags)
            .Concat(SplitTagsForInsight(plan.ConstellationName.Replace("/", ",")))
            .Concat(SplitTagsForInsight(plan.Title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return processTags.Count(tag => planTags.Contains(tag));
    }

    private static HashSet<string> InferProcessTags(string processName)
    {
        var normalized = processName.ToLowerInvariant();
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ContainsAny(normalized, "code", "devenv", "rider", "visualstudio"))
        {
            tags.UnionWith(["development", "code", "debug"]);
        }

        if (ContainsAny(normalized, "chrome", "msedge", "firefox", "browser"))
        {
            tags.UnionWith(["research", "learn", "web"]);
        }

        if (ContainsAny(normalized, "word", "onenote", "obsidian", "notion"))
        {
            tags.UnionWith(["writing", "notes", "learn"]);
        }

        if (ContainsAny(normalized, "excel", "powerbi"))
        {
            tags.UnionWith(["data", "analysis"]);
        }

        if (ContainsAny(normalized, "python", "jupyter", "anaconda"))
        {
            tags.UnionWith(["development", "code", "learn", "ml", "dl"]);
        }

        return tags;
    }

    private static IEnumerable<string> SplitTagsForInsight(string tags)
    {
        return tags
            .Split([' ', ',', '/', '\\', '|', ';', ':', '，', '、', '；', '：'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => tag.Trim().TrimStart('#').ToLowerInvariant())
            .Where(tag => tag.Length >= 2);
    }

    private static void AppendSection(StringBuilder builder, string title, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine(title);
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item)).Take(8))
        {
            builder.AppendLine("• " + item.Trim());
        }
    }

    private static void AppendMarkdownList(StringBuilder builder, string title, IReadOnlyCollection<string> items)
    {
        builder.AppendLine(title);
        if (items.Count == 0)
        {
            builder.AppendLine("- 无");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item)).Take(12))
        {
            builder.AppendLine("- " + item.Trim());
        }

        builder.AppendLine();
    }

    private static string CreateFileToken(string text)
    {
        var token = new string(text
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Take(32)
            .ToArray());
        return string.IsNullOrWhiteSpace(token) ? "digest" : token;
    }

    private void SetMonitorEnabled(bool enabled)
    {
        if (_settingsService.Settings.MonitorEnabled == enabled)
        {
            RefreshMonitorStateProperties();
            return;
        }

        ToggleMonitor();
    }

    private void SetFocusGoal(string goal)
    {
        var normalized = goal.Trim();
        CurrentFocusGoal = normalized;
        _settingsService.Settings.FocusGoal = normalized;
        _settingsService.Save();
        StatusText = T("Main_TaskGoalUpdated");
        OnPropertyChanged(nameof(CurrentFocusGoalDisplay));
    }

    private void SyncFocusGoalFromSettings()
    {
        CurrentFocusGoal = _settingsService.Settings.FocusGoal;
        OnPropertyChanged(nameof(CurrentFocusGoalDisplay));
        RefreshMonitorStateProperties();
    }

    private void RefreshMonitorStateProperties()
    {
        OnPropertyChanged(nameof(IsMonitorEnabled));
        OnPropertyChanged(nameof(MonitorButtonText));
        OnPropertyChanged(nameof(MonitoringStateText));
    }

    private void OnSettingsChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefreshMonitorStateProperties);
            return;
        }

        RefreshMonitorStateProperties();
    }

    private void OnFocusModeStateChanged()
    {
        ResetFocusIntervention();
        IsFocusModeActive = _focusModeService.IsActive;
        OnPropertyChanged(nameof(CurrentFocusGoalDisplay));
        OnPropertyChanged(nameof(FocusModeButtonText));
        OnPropertyChanged(nameof(FocusModeStatusText));
    }

    private void EndFocusModeIfMatches(int memoryId)
    {
        if (_focusModeService.TaskMemoryId == memoryId)
        {
            _focusModeService.Stop();
            ConversationMessages.Add(ConversationMessage.Assistant(T("Main_FocusModeEnded")));
        }
    }

    private void StartAssistantThinking()
    {
        _assistantThinkingIndex = Random.Shared.Next(0, 4);
        AssistantThinkingText = T($"Main_AssistantThinking{_assistantThinkingIndex + 1}");
        IsAssistantThinking = true;
        _assistantThinkingTimer.Start();
    }

    private void StopAssistantThinking()
    {
        _assistantThinkingTimer.Stop();
        IsAssistantThinking = false;
    }

    private void AdvanceAssistantThinkingText()
    {
        _assistantThinkingIndex = (_assistantThinkingIndex + 1) % 4;
        AssistantThinkingText = T($"Main_AssistantThinking{_assistantThinkingIndex + 1}");
    }

    private void SeedConversation()
    {
        ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantSeed")));
    }

    public void ShowBreakpointSnapshot(BreakpointSnapshot snapshot)
    {
        HasConversationStarted = true;
        IsGalaxyVisible = false;
        var returned = snapshot.ReturnedAt?.ToString("HH:mm", CultureInfo.CurrentCulture)
            ?? DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
        var title = string.IsNullOrWhiteSpace(snapshot.WindowTitle)
            ? snapshot.ProcessName
            : snapshot.WindowTitle;
        var planLine = string.IsNullOrWhiteSpace(snapshot.RelatedPlanTitle)
            ? "没有强行写进星图，我只把这次断点留在本地卡点记录里。"
            : $"相关计划：{snapshot.RelatedPlanTitle}";
        var codingSnapshot = GetRecentCodingClientSnapshot();
        var card = CreateBreakpointResumeCard(snapshot, codingSnapshot, title, returned);

        var message =
            $"欢迎回来。你大约在 {snapshot.LeftAt:HH:mm} 离开，{returned} 回来。\n\n" +
            $"{planLine}\n\n" +
            "我把现场整理成一张续做卡片了，先从最容易恢复的那一步开始。";

        ConversationMessages.Add(ConversationMessage.AssistantWithBreakpointCard(message, card));
        StatusText = "已唤出刚才的卡点提示";
    }

    private CodingClientActivitySnapshot? GetRecentCodingClientSnapshot()
    {
        var snapshot = _latestCodingClientSnapshot;
        if (snapshot == null ||
            snapshot.State == CodingClientActivityState.Idle ||
            DateTime.Now - snapshot.UpdatedAt > TimeSpan.FromMinutes(30))
        {
            return null;
        }

        return snapshot;
    }

    private static BreakpointResumeCardViewModel CreateBreakpointResumeCard(
        BreakpointSnapshot snapshot,
        CodingClientActivitySnapshot? codingSnapshot,
        string title,
        string returned)
    {
        var clientText = codingSnapshot == null
            ? "Coding client：没有捕捉到最近生成事件"
            : $"Coding client：{codingSnapshot.ClientName} · {FormatCodingState(codingSnapshot.State)}";
        var workspaceText = codingSnapshot == null || string.IsNullOrWhiteSpace(codingSnapshot.WorkspaceRoot)
            ? "Workspace：暂未识别"
            : $"Workspace：{ShortenPath(codingSnapshot.WorkspaceRoot)}";
        var recentChange = codingSnapshot == null || string.IsNullOrWhiteSpace(codingSnapshot.LastChangedPath)
            ? "最近变动：暂无明确文件变动"
            : $"最近变动：{ShortenPath(codingSnapshot.LastChangedPath)}";
        var status = codingSnapshot?.State switch
        {
            CodingClientActivityState.WaitingForConfirmation => "状态：正在等你确认",
            CodingClientActivityState.Completed => "状态：代码生成已完成，可以检查 diff",
            CodingClientActivityState.Coding => "状态：还在生成中",
            _ => "状态：只恢复桌面断点"
        };
        var nextStep = !string.IsNullOrWhiteSpace(snapshot.NextStep)
            ? snapshot.NextStep
            : codingSnapshot?.State == CodingClientActivityState.Completed
                ? "先打开对应 workspace 看最近 diff，再决定是否运行验证。"
                : "先回到刚才的窗口，确认还要不要把这个现场保存成 plan。";

        return new BreakpointResumeCardViewModel(
            "断点续做卡片",
            $"{snapshot.LeftAt:HH:mm} 离开 · {returned} 回来",
            clientText,
            workspaceText,
            $"活跃进程：{snapshot.ProcessName} · {title}",
            recentChange,
            status,
            nextStep);
    }

    private static string FormatCodingState(CodingClientActivityState state)
    {
        return state switch
        {
            CodingClientActivityState.Coding => "生成中",
            CodingClientActivityState.WaitingForConfirmation => "等待确认",
            CodingClientActivityState.Completed => "已完成",
            _ => "空闲"
        };
    }

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "未识别";
        }

        var normalized = path.Trim();
        var name = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            return normalized;
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(normalized) ?? string.Empty);
        return string.IsNullOrWhiteSpace(parent) ? name : $"{parent}\\{name}";
    }

    private static bool ContainsAny(string text, params string[] candidates)
    {
        return candidates.Any(text.Contains);
    }

    private static bool LooksLikeExplicitMemory(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return normalized.StartsWith("remember that", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("remember:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("记住", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("请记住", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("帮我记住", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateLocalContextReply(
        IReadOnlyList<ContextMemory> memories,
        ForegroundFocusSnapshot? snapshot)
    {
        var appLine = snapshot == null
            ? string.Empty
            : $"\n{T("Main_CurrentContext")}: {snapshot.ProcessName}";
        if (memories.Count == 0)
        {
            return T("Main_AssistantNoMemoryMatched") + appLine;
        }

        var top = memories.Take(3).Select(memory =>
        {
            var plan = memory.IsPlan
                ? memory.IsCompleted ? "（plan 已完成）" : "（plan 未完成）"
                : string.Empty;
            return $"• {memory.Title}{plan}: {memory.Preview}";
        });
        return T("Main_AssistantMemoryMatched") + "\n" + string.Join("\n", top) + appLine;
    }

    private void RefreshComputedStats()
    {
        OnPropertyChanged(nameof(ApplicationCount));
        OnPropertyChanged(nameof(ProductiveCount));
        OnPropertyChanged(nameof(DistractingCount));
        OnPropertyChanged(nameof(UnknownCount));
        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(RecentSessionCountText));
        OnPropertyChanged(nameof(ProductiveShareText));
        OnPropertyChanged(nameof(ApplicationCountText));
        OnPropertyChanged(nameof(FocusTaskCountText));
        OnPropertyChanged(nameof(MemoryCountText));
        AssistantThinkingText = T($"Main_AssistantThinking{_assistantThinkingIndex + 1}");
        OnPropertyChanged(nameof(FocusModeButtonText));
        OnPropertyChanged(nameof(FocusModeStatusText));
    }

    private void RefreshUiTextProperties()
    {
        OnPropertyChanged(nameof(MonitorButtonText));
        OnPropertyChanged(nameof(MonitoringStateText));
        OnPropertyChanged(nameof(CurrentFocusGoalDisplay));
        OnPropertyChanged(nameof(ProductiveShareText));
        OnPropertyChanged(nameof(ApplicationCountText));
        OnPropertyChanged(nameof(FocusTaskCountText));
        OnPropertyChanged(nameof(MemoryCountText));
        OnPropertyChanged(nameof(FocusModeButtonText));
        OnPropertyChanged(nameof(FocusModeStatusText));
    }

    partial void OnMemoryPreviewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGalaxyPreviewMode));
        OnPropertyChanged(nameof(IsMemoryConstellationLayer));
        OnPropertyChanged(nameof(IsMemoryNodeLayer));
        OnPropertyChanged(nameof(IsGalaxyEmpty));
        OnPropertyChanged(nameof(IsFishbonePreviewMode));
    }

    partial void OnContextMemoriesChanged(ObservableCollection<ContextMemory> value)
    {
        OnPropertyChanged(nameof(MemoryCountText));
        OnPropertyChanged(nameof(CurrentFocusGoalDisplay));
        OnPropertyChanged(nameof(FocusModeStatusText));
        OnPropertyChanged(nameof(IsGalaxyEmpty));
    }

    partial void OnMemoryConstellationsChanged(ObservableCollection<MemoryConstellationNodeViewModel> value)
    {
        OnPropertyChanged(nameof(MemoryCountText));
        OnPropertyChanged(nameof(IsGalaxyEmpty));
    }

    partial void OnSelectedGalaxyMemoryChanged(ContextMemory? value)
    {
        OnPropertyChanged(nameof(HasSelectedGalaxyTask));

        if (value == null)
        {
            GalaxyEditTitle = string.Empty;
            GalaxyEditTags = string.Empty;
            GalaxyEditGroup = string.Empty;
            GalaxyEditCreatedAtText = string.Empty;
            GalaxyEditIsCompleted = false;
            GalaxyEditIsAbandoned = false;
            OnPropertyChanged(nameof(HasSelectedPendingMemory));
            return;
        }

        GalaxyEditTitle = value.Title;
        GalaxyEditTags = value.Tags;
        GalaxyEditGroup = value.ConstellationName;
        GalaxyEditCreatedAtText = value.UpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        GalaxyEditIsCompleted = value.IsPlan && value.IsCompleted;
        GalaxyEditIsAbandoned = value.IsPlan && value.IsAbandoned;
        OnPropertyChanged(nameof(HasSelectedPendingMemory));
    }

    partial void OnGalaxyEditIsCompletedChanged(bool value)
    {
        if (value && GalaxyEditIsAbandoned)
        {
            GalaxyEditIsAbandoned = false;
        }
    }

    partial void OnGalaxyEditIsAbandonedChanged(bool value)
    {
        if (value && GalaxyEditIsCompleted)
        {
            GalaxyEditIsCompleted = false;
        }
    }

    partial void OnPendingContextMemoriesChanged(ObservableCollection<ContextMemory> value)
    {
        OnPropertyChanged(nameof(HasPendingMemories));
        OnPropertyChanged(nameof(PendingMemoryCountText));
        OnPropertyChanged(nameof(MemoryCountText));
    }

    partial void OnGalaxySearchTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _expandedMemoryConstellation = string.Empty;
        }

        ApplyGalaxyMemoryFilters();
        OnPropertyChanged(nameof(HasGalaxyFilters));
    }

    partial void OnGalaxyTagFilterChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _expandedMemoryConstellation = string.Empty;
        }

        ApplyGalaxyMemoryFilters();
        OnPropertyChanged(nameof(HasGalaxyFilters));
    }

    partial void OnGalaxyGroupFilterChanged(string value)
    {
        var groupFilter = NormalizeGalaxyFilter(value, "group");
        if (!string.Equals(groupFilter, _expandedMemoryConstellation, StringComparison.OrdinalIgnoreCase))
        {
            _expandedMemoryConstellation = string.Empty;
        }

        ApplyGalaxyMemoryFilters();
        OnPropertyChanged(nameof(HasGalaxyFilters));
    }

    private static string T(string key) => TranslationService.Instance[key];

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return string.Format(T("Main_HoursMinutesFormat"), (int)duration.TotalHours, duration.Minutes);
        }

        if (duration.TotalMinutes >= 1)
        {
            return string.Format(T("Main_MinutesFormat"), Math.Max(1, (int)Math.Round(duration.TotalMinutes)));
        }

        return string.Format(T("Main_SecondsFormat"), Math.Max(0, (int)Math.Round(duration.TotalSeconds)));
    }
}
