using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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
    private string _lastCodingReviewSignature = string.Empty;
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
    private bool _isCompanionRoomVisible;

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

    [ObservableProperty]
    private ObservableCollection<PetGrowthDimensionViewModel> _petGrowthDimensions = new();

    [ObservableProperty]
    private ObservableCollection<PetAbilityBadgeViewModel> _petAbilityBadges = new();

    [ObservableProperty]
    private ObservableCollection<PetRoomItemViewModel> _petRoomItems = new();

    [ObservableProperty]
    private string _petGrowthStageTitle = "观察期";

    [ObservableProperty]
    private string _petGrowthStageDescription = "桌宠正在学习用本地、可解释的信号陪伴你。";

    [ObservableProperty]
    private string _petGrowthProgressText = string.Empty;

    [ObservableProperty]
    private string _petRoomSummaryText = string.Empty;

    [ObservableProperty]
    private string _petUnlockedBadgeCountText = string.Empty;

    [ObservableProperty]
    private BitmapSource? _petRoomSpriteSource;

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
        await RefreshPetGrowthProfileAsync();

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
            var latestTask = await GetLatestActiveFocusTaskAsync();
            if (latestTask == null)
            {
                ConversationMessages.Add(ConversationMessage.Assistant(T("Main_FocusModeNoPlan")));
                return;
            }

            _focusModeService.Start(
                latestTask.Title,
                latestTask.Tags,
                latestTask.NextAction,
                null);
            SetMonitorEnabled(true);
            ConversationMessages.Add(ConversationMessage.Assistant(
                $"专注模式已开启：我没有找到明确的 open plan，所以接上最新生成的任务「{latestTask.Title}」。"));
            return;
        }

        _focusModeService.Start(memory);
        SetMonitorEnabled(true);
        ConversationMessages.Add(ConversationMessage.Assistant(
            string.Format(T("Main_FocusModeStartedFormat"), memory.Title)));
    }

    private async Task<FocusTask?> GetLatestActiveFocusTaskAsync()
    {
        var tasks = FocusTasks.Count > 0
            ? FocusTasks
            : new ObservableCollection<FocusTask>(await _dbService.GetFocusTasksAsync());
        return tasks
            .Where(task => task.Status == FocusTaskStatus.Active)
            .OrderByDescending(task => task.CreatedAt)
            .FirstOrDefault();
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
        IsCompanionRoomVisible = false;
        if (PendingContextMemories.Count > 0)
        {
            SelectedGalaxyMemory = PendingContextMemories[0];
        }
    }

    public void OpenCompanionRoom()
    {
        _ = ShowCompanionRoomAsync();
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
        if (snapshot.State == CodingClientActivityState.Completed)
        {
            _ = ShowCodingReviewAsync(snapshot);
        }
    }

    private async Task ShowCodingReviewAsync(CodingClientActivitySnapshot snapshot)
    {
        var signature = $"{snapshot.ClientKind}|{snapshot.WorkspaceRoot}|{snapshot.LastChangedPath}|{snapshot.UpdatedAt:yyyyMMddHHmmss}";
        if (string.Equals(_lastCodingReviewSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _lastCodingReviewSignature = signature;
        try
        {
            var card = await CreateCodingReviewCardAsync(snapshot);
            HasConversationStarted = true;
            ConversationMessages.Add(ConversationMessage.AssistantWithCodingReviewCard(
                $"{snapshot.ClientName} 刚完成了一轮生成。我先把检查顺序收成一张卡，避免直接被“完成了”三个字带跑。",
                card));
            _ = ExpireCodingPreviewAfterDelayAsync(card);
            StatusText = "AI 编程完成检查已生成";
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
        }
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
            ? $"先回到「{task}」，确认它现在最需要的是继续、验收，还是暂时放下。"
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
            IsCompanionRoomVisible = false;
        }
    }

    [RelayCommand]
    private async Task ToggleCompanionRoom()
    {
        if (IsCompanionRoomVisible)
        {
            IsCompanionRoomVisible = false;
            return;
        }

        await ShowCompanionRoomAsync();
    }

    private async Task ShowCompanionRoomAsync()
    {
        IsCompanionRoomVisible = true;
        IsGalaxyVisible = false;
        IsStatisticsVisible = false;
        HasConversationStarted = true;
        await RefreshPetGrowthProfileAsync();
    }

    [RelayCommand]
    private async Task GenerateDailyReview()
    {
        HasConversationStarted = true;
        var progressMessage = ConversationMessage.AssistantInterruptible("正在生成每日总结。你可以先让我停下，等想清楚再继续。");
        ConversationMessages.Add(progressMessage);
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
                    progressMessage.Text = "每日总结已打断。";
                    progressMessage.StopStreaming();
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
            var taskCapsulePath = await ExportDailyTaskCapsuleAsync(insightMemories, sessions, insight);
            await _dbService.RefreshMemoryLifecycleForDailyReviewAsync();
            await RefreshContextMemoriesAsync();
            await RefreshPetGrowthProfileAsync();

            review ??= CreateFallbackDailyReview(memories, sessions);
            var statsSnapshot = CreateDailyReviewUsageStatsSnapshot(sessions);
            var reviewCard = CreateDailyReviewCard(review, memories, sessions, archivePath ?? string.Empty, taskCapsulePath ?? string.Empty);
            progressMessage.StopStreaming();
            ConversationMessages.Remove(progressMessage);
            ConversationMessages.Add(ConversationMessage.AssistantWithDailyReviewCard(
                T("Main_DailyReviewReady"),
                reviewCard,
                statsSnapshot.HasSlices ? statsSnapshot : null));
            StatusText = T("Main_DailyReviewReady");
        }
        catch (OperationCanceledException)
        {
            progressMessage.Text = "每日总结已打断。";
            progressMessage.StopStreaming();
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
        await RefreshPetGrowthProfileAsync();
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
        await RefreshPetGrowthProfileAsync();
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
        await RefreshPetGrowthProfileAsync();
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

        if (ContainsAny(normalized, "任务预览", "任务债务", "清理任务", "task preview", "task debt"))
        {
            await ShowTaskPreviewAsync();
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
            "coding_review" => "已根据最近的 AI 编程活动、工作区变更和本地计划生成检查建议。",
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

    private async Task<string?> ExportDailyTaskCapsuleAsync(
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        DesktopContextInsight insight)
    {
        var taskMemories = memories
            .Where(memory => memory.IsPlan)
            .OrderByDescending(memory => !memory.IsCompleted && !memory.IsAbandoned)
            .ThenByDescending(memory => memory.UpdatedAt)
            .Take(16)
            .ToList();
        if (taskMemories.Count == 0)
        {
            return null;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perelegans",
            "memories",
            "task-capsules");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd}.task-capsule.md");
        var markdown = BuildDailyTaskCapsuleMarkdown(taskMemories, sessions, insight);
        await File.WriteAllTextAsync(path, markdown, Encoding.UTF8);
        return path;
    }

    private static string BuildDailyTaskCapsuleMarkdown(
        IReadOnlyCollection<ContextMemory> taskMemories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        DesktopContextInsight insight)
    {
        var memoryList = taskMemories.ToList();
        var openTasks = memoryList
            .Where(memory => !memory.IsCompleted && !memory.IsAbandoned)
            .OrderByDescending(memory => memory.UpdatedAt)
            .Take(10)
            .ToList();
        var completedTasks = memoryList
            .Where(memory => memory.IsCompleted)
            .OrderByDescending(memory => memory.CompletedAt ?? memory.UpdatedAt)
            .Take(6)
            .ToList();
        var parkedTasks = memoryList
            .Where(memory => memory.IsAbandoned)
            .OrderByDescending(memory => memory.AbandonedAt ?? memory.UpdatedAt)
            .Take(4)
            .ToList();
        var anchorTask = openTasks.FirstOrDefault() ?? completedTasks.FirstOrDefault() ?? memoryList.FirstOrDefault();
        var mainLine = CreateTaskCapsuleMainLine(anchorTask, memoryList, sessions, insight);

        var builder = new StringBuilder();
        builder.AppendLine($"# {DateTime.Now:yyyy-MM-dd} 任务胶囊");
        builder.AppendLine();
        builder.AppendLine("这不是一份成绩单，只是给稍后回来的你留一张能接上手感的小纸条。");
        builder.AppendLine($"今天这条线，我先替你握住：{mainLine}");
        builder.AppendLine();

        if (openTasks.Count > 0)
        {
            builder.AppendLine("## 还开着的任务");
            foreach (var memory in openTasks)
            {
                builder.AppendLine($"- [ ] {memory.Title}");
                builder.AppendLine($"  - 我会先从这里接你：{CreateContextualTaskDebtHint(memory, memoryList, sessions)}");
                builder.AppendLine($"  - 现场感：{CreateTaskCapsuleMemoryMeta(memory)}");
                if (!string.IsNullOrWhiteSpace(memory.Tags))
                {
                    builder.AppendLine($"  - 线索：{memory.Tags.Trim()}");
                }
            }

            builder.AppendLine();
        }

        if (completedTasks.Count > 0)
        {
            builder.AppendLine("## 已经收好的");
            foreach (var memory in completedTasks)
            {
                var completedAt = memory.CompletedAt.HasValue
                    ? $"，完成于 {memory.CompletedAt.Value:MM-dd HH:mm}"
                    : string.Empty;
                builder.AppendLine($"- [x] {memory.Title}{completedAt}");
            }

            builder.AppendLine();
        }

        if (parkedTasks.Count > 0)
        {
            builder.AppendLine("## 可以先放过它们");
            foreach (var memory in parkedTasks)
            {
                builder.AppendLine($"- {memory.Title}：已经搁置，不用继续占用今天的注意力。");
            }

            builder.AppendLine();
        }

        var topProcesses = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Duration = group.Aggregate(TimeSpan.Zero, (total, session) => total + session.Duration)
            })
            .OrderByDescending(item => item.Duration)
            .Take(5)
            .ToList();
        if (topProcesses.Count > 0)
        {
            builder.AppendLine("## 今天留下的现场感");
            foreach (var process in topProcesses)
            {
                builder.AppendLine($"- {process.ProcessName} 陪了你大约 {FormatDurationForStats(process.Duration)}。这条记录只是证据，不是审判。");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## 我替你看了一眼记忆");
        foreach (var item in CreateMemoryIntegrityItems(memoryList))
        {
            builder.AppendLine($"- {item}");
        }

        builder.AppendLine();
        builder.AppendLine("## 回来时先做这个");
        builder.AppendLine($"- {CreateTaskCapsuleReturnStep(anchorTask, memoryList, sessions, insight)}");

        return builder.ToString().TrimEnd();
    }

    private static string CreateTaskCapsuleMainLine(
        ContextMemory? anchorTask,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        DesktopContextInsight insight)
    {
        if (anchorTask != null && !anchorTask.IsCompleted && !anchorTask.IsAbandoned)
        {
            return $"先把「{anchorTask.Title}」接回来。{CreateContextualTaskDebtHint(anchorTask, memories, sessions)}";
        }

        if (!string.IsNullOrWhiteSpace(insight.SuggestedNextAction))
        {
            return insight.SuggestedNextAction.Trim();
        }

        if (!string.IsNullOrWhiteSpace(insight.Summary))
        {
            return insight.Summary.Trim();
        }

        return "今天的任务线索不算多，先从最新那条未完成记忆开始就好。";
    }

    private static string CreateTaskCapsuleMemoryMeta(ContextMemory memory)
    {
        var updated = memory.UpdatedAt.Date == DateTime.Now.Date
            ? $"今天 {memory.UpdatedAt:HH:mm} 还碰过"
            : $"上次更新在 {memory.UpdatedAt:MM-dd HH:mm}";
        var weight = memory.Weight >= 0.75
            ? "它现在挺重要"
            : memory.Weight <= 0.35
                ? "它可以轻一点处理"
                : "它还在中间地带";
        return $"{updated}，{weight}";
    }

    private static string CreateTaskCapsuleReturnStep(
        ContextMemory? anchorTask,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        DesktopContextInsight insight)
    {
        if (anchorTask != null && !anchorTask.IsCompleted && !anchorTask.IsAbandoned)
        {
            return $"打开「{anchorTask.Title}」相关窗口，先只确认一件事：{CreateContextualTaskDebtHint(anchorTask, memories, sessions)}";
        }

        if (!string.IsNullOrWhiteSpace(insight.SuggestedNextAction))
        {
            return insight.SuggestedNextAction.Trim();
        }

        return "扫一眼还开着的任务，把已经不想做的先标成搁置，注意力会立刻清爽一点。";
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
        await RefreshPetGrowthProfileAsync();
        IsGalaxyVisible = true;
        IsCompanionRoomVisible = false;
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
                await RefreshPetGrowthProfileAsync();
                EndFocusModeIfMatches(completedMemory.Id);
                IsGalaxyVisible = true;
                IsCompanionRoomVisible = false;
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
        await RefreshPetGrowthProfileAsync();
        IsGalaxyVisible = true;
        IsCompanionRoomVisible = false;
    }

    private async Task ShowTaskPreviewAsync()
    {
        HasConversationStarted = true;
        var memories = await _dbService.GetContextMemoriesAsync();
        var sessions = await _dbService.GetApplicationUsageSessionsSinceAsync(DateTime.Now.AddHours(-24));
        var openPlans = memories
            .Where(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .ToList();
        DailyReviewDraft? review = null;
        if (_focusClient?.IsConfigured == true)
        {
            try
            {
                review = await _focusClient.CreateTaskPreviewAsync(memories, FocusTasks, sessions);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        review ??= CreateFallbackTaskPreview(memories, sessions, openPlans);
        var card = CreateDailyReviewCard(review, memories, sessions, string.Empty);
        ConversationMessages.Add(ConversationMessage.AssistantWithDailyReviewCard("我把现在摊开的任务重新看了一遍。", card));
    }

    private static DailyReviewDraft CreateFallbackTaskPreview(
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        IReadOnlyCollection<ContextMemory> openPlans)
    {
        var topProcess = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Minutes = Math.Max(1, (int)Math.Round(group.Sum(session => session.Duration.TotalMinutes)))
            })
            .OrderByDescending(item => item.Minutes)
            .FirstOrDefault();
        var completedToday = memories
            .Where(memory => memory.IsPlan && memory.IsCompleted && memory.CompletedAt?.Date == DateTime.Today)
            .Select(memory => memory.Title)
            .Take(4)
            .ToList();

        return new DailyReviewDraft
        {
            Review = openPlans.Count == 0
                ? "现在没有悬挂的 open plan，桌面反而比较干净。可以不用急着新开一条线，先确认手上有没有刚做完但还没记录的东西。"
                : topProcess == null
                    ? $"当前还有 {openPlans.Count} 个 open plan。它们更像几张摊在桌上的便签：有些要继续，有些可能只是旧想法还没被放下。"
                    : $"当前还有 {openPlans.Count} 个 open plan；最近最显眼的现场是 {topProcess.ProcessName}（约 {topProcess.Minutes} 分钟）。我会优先把任务和这个现场、最近记忆放在一起看，而不是机械地催你拆任务。",
            Encouragement = openPlans.Count == 0
                ? "这不是空白，是难得的轻一点。没有悬挂任务的时候，Perelegans 可以少说一点，让你自己决定下一条线从哪里开始。"
                : "我先不催你继续冲。现在更有价值的是把重复、含糊、已经失效的任务辨出来，让真正还值得做的那条线更亮一点。",
            Highlights = completedToday.Count > 0
                ? completedToday
                : CreateTaskPreviewHighlights(openPlans, sessions),
            Risks = openPlans
                .OrderBy(memory => memory.UpdatedAt)
                .Select(memory => CreateTaskPreviewRisk(memory, memories, sessions))
                .Take(4)
                .DefaultIfEmpty("暂无需要清理的任务债务。")
                .ToList(),
            SuggestedNextAction = openPlans
                .OrderByDescending(memory => memory.UpdatedAt)
                .Select(memory => $"你想先确认「{memory.Title}」还要继续，还是把它和相近任务合并掉？")
                .FirstOrDefault() ?? "你想把刚才的现场保存成一条新任务，还是先保持轻一点？"
        };
    }

    private static List<string> CreateTaskPreviewHighlights(
        IReadOnlyCollection<ContextMemory> openPlans,
        IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var highlights = new List<string>();
        var newestPlan = openPlans
            .OrderByDescending(memory => memory.UpdatedAt)
            .FirstOrDefault();
        if (newestPlan != null)
        {
            highlights.Add($"最新还亮着的任务是「{newestPlan.Title}」。");
        }

        var topProcesses = sessions
            .GroupBy(session => session.ProcessName)
            .OrderByDescending(group => group.Sum(session => session.Duration.TotalMinutes))
            .Take(2)
            .Select(group => group.Key)
            .ToList();
        if (topProcesses.Count > 0)
        {
            highlights.Add($"最近现场主要落在 {string.Join("、", topProcesses)}，可以当作判断任务是否还活着的线索。");
        }

        return highlights.Count > 0 ? highlights : ["今天还没有明确的完成记录，但也没有必要把空白硬解释成失败。"];
    }

    private static string CreateTaskPreviewRisk(
        ContextMemory plan,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var similar = memories
            .Where(memory => memory.Id != plan.Id && memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .Count(memory => HaveOverlappingTerms(plan, memory));
        if (similar > 0)
        {
            return $"「{plan.Title}」旁边还有 {similar} 条相近 open plan，可能不是缺行动，而是需要合并命名。";
        }

        var matched = sessions
            .Where(session => CalculateProcessPlanAffinity(session.ProcessName, plan) > 0)
            .GroupBy(session => session.ProcessName)
            .OrderByDescending(group => group.Sum(session => session.Duration.TotalMinutes))
            .FirstOrDefault();
        if (matched != null)
        {
            var minutes = Math.Max(1, (int)Math.Round(matched.Sum(session => session.Duration.TotalMinutes)));
            return $"「{plan.Title}」和最近的 {matched.Key} 现场有关系（约 {minutes} 分钟），适合先判断它是不是已经推进到可以验收。";
        }

        var ageDays = Math.Max(0, (DateTime.Now.Date - plan.UpdatedAt.Date).Days);
        if (ageDays >= 3)
        {
            return $"「{plan.Title}」已经安静了 {ageDays} 天，可能需要继续、合并或搁置，而不是继续挂着消耗注意力。";
        }

        if (IsMechanicalNextAction(plan.NextPrediction))
        {
            return $"「{plan.Title}」的下一步还比较泛，最好换成一个能看见对象的判断：文件、窗口、验收点或要删掉的旧分支。";
        }

        return string.IsNullOrWhiteSpace(plan.NextPrediction)
            ? $"「{plan.Title}」还没有清楚的下一步，可以先问自己：它现在是在等验证，还是已经不重要了？"
            : $"「{plan.Title}」下一步看起来是：{plan.NextPrediction.Trim()}";
    }

    [RelayCommand]
    private async Task CompleteTaskDebtItem(TaskDebtItemViewModel? item)
    {
        if (item == null || !item.IsActionable)
        {
            return;
        }

        var completed = await _dbService.CompleteContextMemoryAsync(item.MemoryId);
        if (completed == null)
        {
            return;
        }

        ConversationMessages.Add(ConversationMessage.Assistant($"已把「{completed.Title}」标记完成。"));
        await RefreshContextMemoriesAsync(completed.Id);
        await RefreshPetGrowthProfileAsync();
        EndFocusModeIfMatches(completed.Id);
    }

    [RelayCommand]
    private async Task AbandonTaskDebtItem(TaskDebtItemViewModel? item)
    {
        if (item == null || !item.IsActionable)
        {
            return;
        }

        var abandoned = await _dbService.AbandonContextMemoryAsync(item.MemoryId);
        if (abandoned == null)
        {
            return;
        }

        ConversationMessages.Add(ConversationMessage.Assistant($"已把「{abandoned.Title}」搁置。"));
        await RefreshContextMemoriesAsync(abandoned.Id);
        await RefreshPetGrowthProfileAsync();
        EndFocusModeIfMatches(abandoned.Id);
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

    private DailyReviewDraft CreateFallbackDailyReview(
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var todayTasks = FocusTasks
            .Where(task => task.CreatedAt.Date == DateTime.Today || task.CompletedAt?.Date == DateTime.Today)
            .ToList();
        var completed = todayTasks.Count(task => task.Status == FocusTaskStatus.Completed);
        var active = todayTasks.Count(task => task.Status == FocusTaskStatus.Active);
        var openPlans = memories
            .Where(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .OrderByDescending(memory => memory.UpdatedAt)
            .Take(4)
            .ToList();
        var topProcess = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Minutes = Math.Max(1, (int)Math.Round(group.Sum(session => session.Duration.TotalMinutes)))
            })
            .OrderByDescending(item => item.Minutes)
            .FirstOrDefault();

        return new DailyReviewDraft
        {
            Review = topProcess == null
                ? $"今天完成 {completed} 个任务，还有 {active} 个任务保持活跃。记录不多的时候，不必硬凑结论；先把真实留下的线索收好就够了。"
                : $"今天完成 {completed} 个任务，还有 {active} 个任务保持活跃。桌面现场里 {topProcess.ProcessName} 最显眼（约 {topProcess.Minutes} 分钟），它更像今天主线的一个入口，而不是单纯的时间数字。",
            Encouragement = openPlans.Count > 0
                ? "我看到你没有只是停在想法里，至少已经把几条要推进的线留了下来。现在要做的不是更用力，而是让最重要的一条别被其它便签盖住。"
                : "今天的线索不一定多，但这也可以是休整。Perelegans 先帮你把现场收好，不把空白解释成失败。",
            Highlights = todayTasks
                .Where(task => task.Status == FocusTaskStatus.Completed)
                .Select(task => task.Title)
                .Take(3)
                .DefaultIfEmpty(openPlans.FirstOrDefault()?.Title ?? "今天还没有明确完成项，但主线已经被记录下来。")
                .ToList(),
            Risks = openPlans
                .Select(plan => CreateTaskPreviewRisk(plan, memories, sessions))
                .Take(3)
                .DefaultIfEmpty("今天没有明显任务债务；如果要继续，可以从最近的现场而不是新计划开始。")
                .ToList(),
            SuggestedNextAction = openPlans.Count > 0
                ? $"明天开始时，你想先收掉「{openPlans[0].Title}」，还是先判断它是否该合并到别的任务里？"
                : "明天开场时，你想先延续今天的现场，还是重新挑一条更轻的线？"
        };
    }

    private DailyReviewCardViewModel CreateDailyReviewCard(
        DailyReviewDraft review,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        string archivePath,
        string taskCapsulePath = "")
    {
        var today = DateTime.Today;
        var todayTasks = FocusTasks
            .Where(task => task.CreatedAt.Date == today || task.CompletedAt?.Date == today)
            .ToList();
        var memorySignals = _allVisibleContextMemories.Count > 0
            ? _allVisibleContextMemories
            : memories;
        var completedTasks = todayTasks.Count(task => task.Status == FocusTaskStatus.Completed);
        var activeTasks = todayTasks.Count(task => task.Status == FocusTaskStatus.Active);
        var openPlans = memorySignals.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned);
        var updatedMemories = memorySignals.Count(memory => memory.UpdatedAt.Date == today || memory.CreatedAt.Date == today);
        var topProcess = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Duration = group.Aggregate(TimeSpan.Zero, (total, session) => total + session.Duration)
            })
            .OrderByDescending(item => item.Duration)
            .FirstOrDefault();

        var metrics = new List<DailyReviewMetricViewModel>
        {
            new("done", completedTasks.ToString(CultureInfo.CurrentCulture), "完成任务"),
            new("open", activeTasks.ToString(CultureInfo.CurrentCulture), "进行中"),
            new("memory", updatedMemories.ToString(CultureInfo.CurrentCulture), "今日记忆线索")
        };
        if (topProcess != null)
        {
            metrics.Add(new(
                "focus",
                FormatDurationForStats(topProcess.Duration),
                topProcess.ProcessName));
        }
        else if (openPlans > 0)
        {
            metrics.Add(new("plan", openPlans.ToString(CultureInfo.CurrentCulture), "未完成 plan"));
        }

        var savedContextText = string.IsNullOrWhiteSpace(archivePath)
            ? string.Empty
            : $"上下文已保存：{Path.GetFileName(archivePath)}";

        return new DailyReviewCardViewModel(
            T("Main_DailyReview"),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            T("Main_DailyReviewEncouragement"),
            NormalizeReviewCardText(review.Encouragement, T("Main_DailyReviewFallbackEncouragement")),
            T("Main_DailyReviewOverview"),
            NormalizeReviewCardText(review.Review, string.Format(T("Main_FallbackDailyReviewFormat"), completedTasks, activeTasks)),
            T("Main_DailyReviewHighlights"),
            NormalizeReviewCardItems(review.Highlights, T("Main_DailyReviewNoCompletedTasks")),
            T("Main_DailyReviewRisks"),
            NormalizeReviewCardItems(review.Risks, T("Main_DailyReviewNoRisk")),
            T("Main_DailyReviewNextAction"),
            NormalizeReviewCardText(review.SuggestedNextAction, T("Main_DailyReviewFallbackNextAction")),
            metrics,
            savedContextText,
            CreateTaskDebtItems(memorySignals, sessions),
            CreateMemoryIntegrityItems(memorySignals),
            taskCapsulePath);
    }

    private static IReadOnlyList<TaskDebtItemViewModel> CreateTaskDebtItems(
        IEnumerable<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var memoryList = memories.ToList();
        return memories
            .Where(memory => memory.IsPlan)
            .OrderBy(memory => memory.IsCompleted || memory.IsAbandoned)
            .ThenByDescending(memory => memory.UpdatedAt)
            .Take(8)
            .Select(memory =>
            {
                var ageDays = Math.Max(0, (DateTime.Now.Date - memory.UpdatedAt.Date).Days);
                var status = memory.IsCompleted
                    ? "已完成"
                    : memory.IsAbandoned
                        ? "已搁置"
                        : ageDays >= 3
                            ? $"悬停 {ageDays} 天"
                            : "进行中";
                var next = CreateContextualTaskDebtHint(memory, memoryList, sessions);
                return new TaskDebtItemViewModel(
                    memory.Id,
                    memory.Title,
                    $"{status} · {next}",
                    memory.IsCompleted,
                    !memory.IsCompleted && !memory.IsAbandoned);
            })
            .ToList();
    }

    private static string CreateContextualTaskDebtHint(
        ContextMemory memory,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        if (memory.IsCompleted)
        {
            return memory.CompletedAt.HasValue
                ? $"完成于 {memory.CompletedAt.Value:MM-dd HH:mm}，可以安心留作产出证据。"
                : "这条线已经收束，可以作为产出证据留着。";
        }

        if (memory.IsAbandoned)
        {
            return "已经搁置，不必再占用今天的注意力。";
        }

        var similar = memories
            .Where(other => other.Id != memory.Id && other.IsPlan && !other.IsCompleted && !other.IsAbandoned)
            .Count(other => HaveOverlappingTerms(memory, other));
        if (similar > 0)
        {
            return $"旁边有 {similar} 条相近任务，先考虑合并或改名。";
        }

        var matched = sessions
            .Where(session => CalculateProcessPlanAffinity(session.ProcessName, memory) > 0)
            .GroupBy(session => session.ProcessName)
            .OrderByDescending(group => group.Sum(session => session.Duration.TotalMinutes))
            .FirstOrDefault();
        if (matched != null)
        {
            var minutes = Math.Max(1, (int)Math.Round(matched.Sum(session => session.Duration.TotalMinutes)));
            return $"最近和 {matched.Key} 现场有关（约 {minutes} 分钟），适合先验收是否已经推进。";
        }

        if (!IsMechanicalNextAction(memory.NextPrediction))
        {
            return memory.NextPrediction.Trim();
        }

        var title = memory.Title.ToLowerInvariant();
        var tags = memory.Tags.ToLowerInvariant();
        if (ContainsAny(title + " " + tags, "perelegans", "wpf", "xaml", "code", "coding", "development", "开发", "代码"))
        {
            return "先选一个可见验收点：界面、桌宠气泡、构建结果，或最近改动文件。";
        }

        if (ContainsAny(title + " " + tags, "写", "writing", "文档", "文章", "论文"))
        {
            return "先看它现在缺的是结构、材料，还是最后一遍润色。";
        }

        var ageDays = Math.Max(0, (DateTime.Now.Date - memory.UpdatedAt.Date).Days);
        return ageDays >= 3
            ? "先判断它还值得继续，还是该合并进新任务。"
            : "先给它换成一个更具体的对象：文件、窗口、材料或验收点。";
    }

    private static bool HaveOverlappingTerms(ContextMemory left, ContextMemory right)
    {
        var leftTerms = SplitTagsForInsight(string.Join(' ', left.Title, left.Tags, left.ConstellationName))
            .Where(term => term.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTerms.Count == 0)
        {
            return false;
        }

        var rightTerms = SplitTagsForInsight(string.Join(' ', right.Title, right.Tags, right.ConstellationName))
            .Where(term => term.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftTerms.Intersect(rightTerms, StringComparer.OrdinalIgnoreCase).Take(2).Count() >= 2;
    }

    private static bool IsMechanicalNextAction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalized = text.Trim().ToLowerInvariant();
        return ContainsAny(
            normalized,
            "拆成一个",
            "小动作",
            "最小可验证",
            "15 分钟",
            "15分钟",
            "先推进",
            "first visible step",
            "small step");
    }

    private static IReadOnlyList<string> CreateMemoryIntegrityItems(IEnumerable<ContextMemory> memories)
    {
        var items = new List<string>();
        var stale = memories
            .Where(memory => memory.Lifecycle == ContextMemoryLifecycle.Stale)
            .OrderByDescending(memory => memory.UpdatedAt)
            .Take(2)
            .Select(memory => $"可能过期：{memory.Title}");
        items.AddRange(stale);

        var contradictions = memories
            .Where(memory => memory.Lifecycle == ContextMemoryLifecycle.Contradicted)
            .OrderByDescending(memory => memory.UpdatedAt)
            .Take(2)
            .Select(memory => $"需要证伪：{memory.Title}");
        items.AddRange(contradictions);

        var duplicateOpenPlans = memories
            .Where(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .GroupBy(memory => NormalizeTaskKey(memory.Title))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Take(2)
            .Select(group => $"疑似重复 plan：{group.First().Title}（{group.Count()} 条）");
        items.AddRange(duplicateOpenPlans);

        if (items.Count == 0)
        {
            items.Add("今天没有发现明显冲突记忆；过期、重复 plan 会在这里浮出水面。");
        }

        return items.Take(5).ToList();
    }

    private static string NormalizeTaskKey(string title)
    {
        var terms = SplitTagsForInsight(title)
            .Where(term => term.Length > 1)
            .Take(4);
        return string.Join(" ", terms).ToLowerInvariant();
    }

    private static IReadOnlyList<string> NormalizeReviewCardItems(IReadOnlyCollection<string> items, string fallback)
    {
        var normalized = items
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(4)
            .ToList();
        return normalized.Count > 0 ? normalized : [fallback];
    }

    private static string NormalizeReviewCardText(string text, string fallback)
    {
        return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
    }

    private async Task RefreshPetGrowthProfileAsync()
    {
        var memories = await _dbService.GetContextMemoriesAsync(includePending: true, includeRejected: true);
        var breakpoints = await _dbService.GetBreakpointSnapshotsAsync();
        var confirmedMemories = memories.Count(memory =>
            memory.ReviewStatus == ContextMemoryReviewStatus.Confirmed &&
            !IsSceneCheckpointMemory(memory));
        var rejectedMemories = memories.Count(memory => memory.ReviewStatus == ContextMemoryReviewStatus.Rejected);
        var reviewMemories = memories.Count(memory =>
            memory.ReviewStatus == ContextMemoryReviewStatus.Confirmed &&
            memory.Type == ContextMemoryType.Review);
        var completedTasks = FocusTasks.Count(task => task.Status == FocusTaskStatus.Completed);
        var completedPlans = memories.Count(memory =>
            memory.ReviewStatus == ContextMemoryReviewStatus.Confirmed &&
            memory.IsPlan &&
            memory.IsCompleted);
        var shownBreakpoints = breakpoints.Count(snapshot => snapshot.WasShown);

        var memoryValue = confirmedMemories;
        var understandingValue = reviewMemories * 8 + Math.Min(12, confirmedMemories / 4d);
        var actionValue = completedTasks * 3 + completedPlans * 4;
        var companionshipValue = shownBreakpoints * 6 + reviewMemories * 2;
        var restraintValue = 12 + rejectedMemories * 4;

        PetGrowthDimensions = new ObservableCollection<PetGrowthDimensionViewModel>
        {
            new("记忆力", "确认后的线索越多，桌宠越能把上下文放回星图。", memoryValue, 30, "线索", "#FFFF8FC3"),
            new("理解力", "复盘和上下文压缩会让桌宠更会解释节奏。", understandingValue, 40, "点", "#FF79B5E0"),
            new("行动力", "完成任务和 plan 会让桌宠从提醒走向闭环。", actionValue, 30, "点", "#FF9CC978"),
            new("陪伴感", "断点恢复和复盘让桌宠更会在你回来时接住现场。", companionshipValue, 30, "点", "#FFE0B65E"),
            new("克制感", "拒绝不需要的建议也会让桌宠学会少打扰。", restraintValue, 28, "点", "#FFB08DD8")
        };

        var badges = new List<PetAbilityBadgeViewModel>
        {
            new("不窥屏守护", "不依赖截图监视，用本地可解释信号陪伴。", "克制感", 10, restraintValue, "#FFB08DD8"),
            new("星图整理者", "已经能把确认后的线索稳定放入星图。", "记忆力", 8, memoryValue, "#FFFF8FC3"),
            new("复盘伙伴", "能从复盘里整理今日推进、风险和下一问。", "理解力", 16, understandingValue, "#FF79B5E0"),
            new("行动闭环者", "见证过足够多任务从计划走到完成。", "行动力", 12, actionValue, "#FF9CC978"),
            new("断点守护者", "能在离开和回来之间保存上下文现场。", "陪伴感", 6, companionshipValue, "#FFE0B65E"),
            new("安静陪伴者", "会把被忽略的建议当作边界，而不是失败。", "克制感", 20, restraintValue, "#FF66C8BA")
        };
        PetAbilityBadges = new ObservableCollection<PetAbilityBadgeViewModel>(badges);

        var unlocked = badges.Count(badge => badge.IsUnlocked);
        (PetGrowthStageTitle, PetGrowthStageDescription) = CreatePetGrowthStage(unlocked);
        PetGrowthProgressText = $"已解锁 {unlocked}/{badges.Count} 枚能力徽章";
        PetUnlockedBadgeCountText = PetGrowthProgressText;
        PetRoomSummaryText = $"这个小房间由 {confirmedMemories} 条确认记忆、{completedTasks + completedPlans} 个完成闭环、{reviewMemories} 次复盘和 {shownBreakpoints} 次断点恢复搭起来。";
        PetRoomItems = new ObservableCollection<PetRoomItemViewModel>
        {
            new("星图墙", $"{confirmedMemories} 条确认线索正在构成它的长期记忆。", "#FFFF8FC3"),
            new("复盘便签", $"{reviewMemories} 张复盘卡片贴在墙上，帮它理解节奏。", "#FF79B5E0"),
            new("行动架", $"{completedTasks + completedPlans} 个完成闭环让它学会陪你收束。", "#FF9CC978"),
            new("断点灯", $"{shownBreakpoints} 次回到现场让它更会安静守候。", "#FFE0B65E"),
            new("安静角", $"{rejectedMemories} 次忽略建议被记录成边界感。", "#FF66C8BA")
        };
        PetRoomSpriteSource = CreatePetRoomIdleSpriteSource();
    }

    private BitmapSource CreatePetRoomIdleSpriteSource()
    {
        var settings = _settingsService.Settings;
        if (PetSkinPresets.Normalize(settings.FloatingPetSkinId) == PetSkinPresets.Custom &&
            TryCreateCustomPetRoomSpriteSource(settings, out var customSprite))
        {
            return customSprite;
        }

        var skinId = PetSkinPresets.Normalize(settings.FloatingPetSkinId);
        if (TryLoadPackBitmap($"Images/Pet/Skins/{skinId}_idle.png", out var presetSheet))
        {
            return CropPetSpriteFrame(presetSheet, 0, Math.Max(1, presetSheet.PixelWidth / 6), Math.Max(1, presetSheet.PixelHeight), 0);
        }

        if (TryLoadPackBitmap("Images/Pet/pixel_cat_idle.png", out var legacySheet))
        {
            return CropPetSpriteFrame(legacySheet, 0, Math.Max(1, legacySheet.PixelWidth / 6), Math.Max(1, legacySheet.PixelHeight), 0);
        }

        return BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 0 }, 4);
    }

    private static bool TryCreateCustomPetRoomSpriteSource(AppSettings settings, out BitmapSource sprite)
    {
        sprite = null!;
        if (string.IsNullOrWhiteSpace(settings.PetSpritePath) ||
            !Uri.TryCreate(settings.PetSpritePath, UriKind.Absolute, out var uri) ||
            !TryLoadBitmap(uri, out var sheet))
        {
            return false;
        }

        var frameWidth = NormalizePositive(settings.PetSpriteFrameWidth, 48);
        var frameHeight = NormalizePositive(settings.PetSpriteFrameHeight, 48);
        var rowCount = NormalizePositive(settings.PetSpriteRowCount, 1);
        var idleRow = Math.Clamp(settings.PetSpriteIdleRow, 0, rowCount - 1);
        if (sheet.PixelWidth < frameWidth || sheet.PixelHeight < frameHeight * (idleRow + 1))
        {
            return false;
        }

        sprite = CropPetSpriteFrame(sheet, idleRow, frameWidth, frameHeight, 0);
        return true;
    }

    private static BitmapSource CropPetSpriteFrame(BitmapSource sheet, int rowIndex, int frameWidth, int frameHeight, int frameIndex)
    {
        var frame = new CroppedBitmap(
            sheet,
            new Int32Rect(
                Math.Clamp(frameIndex * frameWidth, 0, Math.Max(0, sheet.PixelWidth - frameWidth)),
                Math.Clamp(rowIndex * frameHeight, 0, Math.Max(0, sheet.PixelHeight - frameHeight)),
                Math.Min(frameWidth, sheet.PixelWidth),
                Math.Min(frameHeight, sheet.PixelHeight)));
        frame.Freeze();
        return frame;
    }

    private static bool TryLoadPackBitmap(string path, out BitmapSource image)
    {
        return TryLoadBitmap(new Uri($"pack://application:,,,/{path}", UriKind.Absolute), out image);
    }

    private static bool TryLoadBitmap(Uri uri, out BitmapSource image)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
            return true;
        }
        catch
        {
            image = null!;
            return false;
        }
    }

    private static int NormalizePositive(int value, int fallback)
    {
        return value > 0 ? value : fallback;
    }

    private static (string Title, string Description) CreatePetGrowthStage(int unlockedBadgeCount)
    {
        return unlockedBadgeCount switch
        {
            <= 1 => ("观察期", "桌宠正在学习只用必要、本地、可解释的信号理解你。"),
            2 => ("记忆期", "它已经能把你确认过的线索当作可靠上下文。"),
            3 => ("星图期", "它开始把记忆、复盘和行动连接成更完整的结构。"),
            4 => ("复盘期", "它能把一天里的推进、卡点和下一问整理成可继续的现场。"),
            _ => ("共生期", "它的成长来自你的选择、边界和行动闭环，而不是监视。")
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
            var matchedProcess = sessions
                .Where(session => CalculateProcessPlanAffinity(session.ProcessName, plan) > 0)
                .GroupBy(session => session.ProcessName)
                .OrderByDescending(group => group.Sum(session => session.Duration.TotalMinutes))
                .Select(group => group.Key)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(matchedProcess)
                ? $"先回到「{plan.Title}」，判断它现在是在等验收、等材料，还是已经该搁置。"
                : $"从 {matchedProcess} 的最近现场接回「{plan.Title}」，先确认这条线是否已经推进到可验收。";
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
            dispatcher.Invoke(() =>
            {
                RefreshMonitorStateProperties();
                PetRoomSpriteSource = CreatePetRoomIdleSpriteSource();
            });
            return;
        }

        RefreshMonitorStateProperties();
        PetRoomSpriteSource = CreatePetRoomIdleSpriteSource();
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

    private async Task<CodingReviewCardViewModel> CreateCodingReviewCardAsync(CodingClientActivitySnapshot snapshot)
    {
        var sessions = await _dbService.GetApplicationUsageSessionsSinceAsync(DateTime.Now.AddHours(-2));
        var activePlan = await _dbService.GetLatestOpenPlanMemoryAsync();
        var changedPath = string.IsNullOrWhiteSpace(snapshot.LastChangedPath)
            ? string.Empty
            : snapshot.LastChangedPath.Trim();
        var extension = Path.GetExtension(changedPath);
        var risk = CreateCodingRiskText(changedPath, extension, activePlan);
        var checkpoints = CreateCodingReviewCheckpoints(changedPath, extension, activePlan, sessions);
        var verification = CreateCodingVerificationText(snapshot.WorkspaceRoot, changedPath, extension);
        var resolvedChangedPath = ResolveChangedPath(snapshot.WorkspaceRoot, changedPath);
        var preview = await CreateCodingFilePreviewAsync(snapshot.WorkspaceRoot, resolvedChangedPath);

        return new CodingReviewCardViewModel(
            "AI 编程完成检查",
            DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture),
            $"客户端：{snapshot.ClientName}",
            string.IsNullOrWhiteSpace(snapshot.WorkspaceRoot)
                ? "Workspace：暂未识别"
                : $"Workspace：{ShortenPath(snapshot.WorkspaceRoot)}",
            string.IsNullOrWhiteSpace(changedPath)
                ? "最近变更：暂未捕捉到明确文件"
                : $"最近变更：{ShortenPath(changedPath)}",
            risk,
            checkpoints,
            verification,
            string.IsNullOrWhiteSpace(preview) ? string.Empty : "30 秒 diff / 文件预览",
            preview,
            resolvedChangedPath,
            snapshot.ClientKind.ToString(),
            snapshot.WorkspaceRoot);
    }

    private async Task ExpireCodingPreviewAfterDelayAsync(CodingReviewCardViewModel card)
    {
        if (!card.IsPreviewVisible)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30));
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(card.ExpirePreview);
    }

    [RelayCommand]
    private void OpenCodingReviewClient(CodingReviewCardViewModel? card)
    {
        if (card == null)
        {
            return;
        }

        if (TryActivateCodingClient(card.ClientKind))
        {
            return;
        }

        StatusText = "我没找到对应的编码客户端窗口，可以先把 Codex / Claude Code / OpenCode 打开到前台一次。";
    }

    [RelayCommand]
    private void OpenDailyTaskCapsule(string? path)
    {
        OpenLocalPath(path);
    }

    private static void OpenLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                var startInfo = new ProcessStartInfo("explorer.exe");
                startInfo.ArgumentList.Add($"/select,{path}");
                Process.Start(startInfo);
                return;
            }

            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
        }
    }

    private static bool TryActivateCodingClient(string clientKind)
    {
        var candidates = clientKind switch
        {
            nameof(CodingClientKind.ClaudeDesktop) => new[] { "Claude", "Claude Code", "claude" },
            nameof(CodingClientKind.OpenCodeDesktop) => new[] { "OpenCode", "opencode", "ai.opencode.desktop" },
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
                catch (Exception ex)
                {
                    App.WriteCrashLog(ex);
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
                catch (Exception ex)
                {
                    App.WriteCrashLog(ex);
                }
            }
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static string ResolveChangedPath(string workspaceRoot, string changedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath))
        {
            return string.Empty;
        }

        var trimmed = changedPath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        return string.IsNullOrWhiteSpace(workspaceRoot)
            ? trimmed
            : Path.GetFullPath(Path.Combine(workspaceRoot, trimmed));
    }

    private static async Task<string> CreateCodingFilePreviewAsync(string workspaceRoot, string changedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath) || !File.Exists(changedPath))
        {
            return string.Empty;
        }

        var diff = await TryReadGitDiffAsync(workspaceRoot, changedPath);
        if (!string.IsNullOrWhiteSpace(diff))
        {
            return LimitPreview(diff);
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(changedPath, Encoding.UTF8);
            var preview = string.Join(Environment.NewLine, lines.Take(60));
            return LimitPreview(preview);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            return string.Empty;
        }
    }

    private static async Task<string> TryReadGitDiffAsync(string workspaceRoot, string changedPath)
    {
        var workingDirectory = Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : Path.GetDirectoryName(changedPath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return string.Empty;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("diff");
            startInfo.ArgumentList.Add("--no-ext-diff");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(changedPath);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return string.Empty;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return await outputTask;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string LimitPreview(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').Take(90).ToList();
        var preview = string.Join(Environment.NewLine, lines);
        return preview.Length <= 6000 ? preview : preview[..6000] + Environment.NewLine + "...";
    }

    private static string CreateCodingRiskText(string changedPath, string extension, ContextMemory? activePlan)
    {
        if (string.IsNullOrWhiteSpace(changedPath))
        {
            return "风险：只捕捉到完成事件，没有明确文件路径；建议先看生成工具里的 diff 或变更列表。";
        }

        var planText = activePlan == null ? string.Empty : $"，当前未完成 plan 是「{activePlan.Title}」";
        return extension.ToLowerInvariant() switch
        {
            ".xaml" => $"风险：改到了界面层{planText}；重点检查绑定名、资源键和窄窗口布局。",
            ".cs" => $"风险：改到了 C# 逻辑{planText}；重点检查空值、异步取消、事件订阅和数据库写入路径。",
            ".resx" => $"风险：改到了本地化资源{planText}；重点检查资源键是否两种语言都存在。",
            ".csproj" => $"风险：改到了项目文件{planText}；重点检查包引用、目标框架和发布配置。",
            ".json" or ".toml" or ".xml" => $"风险：改到了配置文件{planText}；重点检查默认值和旧配置兼容。",
            _ => $"风险：AI 生成已完成{planText}；先确认变更是否落在预期范围。"
        };
    }

    private static IReadOnlyList<string> CreateCodingReviewCheckpoints(
        string changedPath,
        string extension,
        ContextMemory? activePlan,
        IReadOnlyCollection<ApplicationUsageSession> sessions)
    {
        var checkpoints = new List<string>();
        if (!string.IsNullOrWhiteSpace(changedPath))
        {
            checkpoints.Add($"先打开 {ShortenPath(changedPath)}，确认改动是否符合你的 prompt。");
        }

        if (activePlan != null)
        {
            checkpoints.Add($"对照当前 plan「{activePlan.Title}」检查有没有偏题或多改。");
        }

        var topProcess = sessions
            .GroupBy(session => session.ProcessName)
            .OrderByDescending(group => group.Sum(session => session.Duration.TotalMinutes))
            .Select(group => group.Key)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(topProcess))
        {
            checkpoints.Add($"最近主要现场在 {topProcess}，优先验证这个工作流会不会被影响。");
        }

        if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            checkpoints.Add("跑一次界面，检查新增文本是否溢出、卡片是否互相遮挡。");
        }
        else if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            checkpoints.Add("重点看异常分支和重复触发，尤其是 timer、event、async void 周边。");
        }

        checkpoints.Add("最后再决定是否把这次产出沉淀成记忆或完成对应 plan。");
        return checkpoints.Take(5).ToList();
    }

    private static string CreateCodingVerificationText(string workspaceRoot, string changedPath, string extension)
    {
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".resx", StringComparison.OrdinalIgnoreCase))
        {
            return "建议验证：先运行 dotnet build，再手动打开相关窗口走一遍刚才的场景。";
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot) || !string.IsNullOrWhiteSpace(changedPath))
        {
            return "建议验证：查看 diff，运行项目现有的最小验证命令，再确认是否需要保存为记忆。";
        }

        return "建议验证：先回到 AI 编程客户端查看完成摘要和 diff，再决定下一步。";
    }

    public void ShowBreakpointSnapshot(BreakpointSnapshot snapshot)
    {
        HasConversationStarted = true;
        IsGalaxyVisible = false;
        IsCompanionRoomVisible = false;
        var returned = snapshot.ReturnedAt?.ToString("HH:mm", CultureInfo.CurrentCulture)
            ?? DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
        var title = string.IsNullOrWhiteSpace(snapshot.WindowTitle)
            ? snapshot.ProcessName
            : snapshot.WindowTitle;
        var codingSnapshot = GetRecentCodingClientSnapshot();
        var card = CreateBreakpointResumeCard(snapshot, codingSnapshot, title, returned);

        var message =
            $"欢迎回来。你大约在 {snapshot.LeftAt:HH:mm} 离开，{returned} 回来。\n\n" +
            "我把刚才的窗口、计划线索和可继续动作压成一颗断点恢复胶囊了。先不用重新解释一遍，从下面三步接回去就行。";

        ConversationMessages.Add(ConversationMessage.AssistantWithBreakpointCard(message, card));
        StatusText = "已生成断点恢复胶囊";
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
            ? "AI 编程状态：未捕捉到最近生成事件"
            : $"AI 编程状态：{codingSnapshot.ClientName} · {FormatCodingState(codingSnapshot.State)}";
        var workspaceText = codingSnapshot == null || string.IsNullOrWhiteSpace(codingSnapshot.WorkspaceRoot)
            ? "工作区：暂未识别"
            : $"工作区：{ShortenPath(codingSnapshot.WorkspaceRoot)}";
        var recentChange = codingSnapshot == null || string.IsNullOrWhiteSpace(codingSnapshot.LastChangedPath)
            ? "最近变动：暂无明确文件变动"
            : $"最近变动：{ShortenPath(codingSnapshot.LastChangedPath)}";
        var status = codingSnapshot?.State switch
        {
            CodingClientActivityState.WaitingForConfirmation => "胶囊状态：先处理权限确认，再恢复工作现场",
            CodingClientActivityState.Completed => "胶囊状态：AI 已完成一轮生成，适合先检查 diff",
            CodingClientActivityState.Coding => "胶囊状态：AI 仍在生成，适合先确认目标和等待完成",
            _ => "胶囊状态：已捕获桌面断点，可以直接续做"
        };
        var resumeSteps = CreateBreakpointResumeStepItems(snapshot, codingSnapshot);
        var nextStep = string.Join(Environment.NewLine, resumeSteps.Select((step, index) => $"{index + 1}. {step}"));
        var evidenceItems = CreateBreakpointEvidenceItems(snapshot, codingSnapshot, title);
        var recoveryPrompt = CreateBreakpointRecoveryPrompt(snapshot, title);
        var capsuleIntro = CreateBreakpointCapsuleIntro(snapshot, title);

        return new BreakpointResumeCardViewModel(
            "断点恢复胶囊",
            $"{snapshot.LeftAt:HH:mm} 离开 · {returned} 回来",
            clientText,
            workspaceText,
            $"离开前现场：{snapshot.ProcessName} · {title}",
            recentChange,
            status,
            nextStep,
            capsuleIntro,
            recoveryPrompt,
            evidenceItems,
            resumeSteps);
    }

    private static IReadOnlyList<string> CreateBreakpointResumeStepItems(
        BreakpointSnapshot snapshot,
        CodingClientActivitySnapshot? codingSnapshot)
    {
        var steps = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.NextStep))
        {
            steps.Add(snapshot.NextStep.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.RelatedPlanTitle))
        {
            steps.Add($"先回到「{snapshot.RelatedPlanTitle}」，写下一句“我刚才停在什么问题上”。");
        }
        else
        {
            steps.Add($"先回到 {snapshot.ProcessName}，用窗口标题恢复刚才的现场。");
        }

        if (codingSnapshot?.State == CodingClientActivityState.Completed)
        {
            var changed = string.IsNullOrWhiteSpace(codingSnapshot.LastChangedPath)
                ? "AI 编程客户端的 diff"
                : ShortenPath(codingSnapshot.LastChangedPath);
            steps.Add($"检查 {changed}，确认 AI 完成的内容没有偏离你的 prompt。");
        }
        else if (codingSnapshot?.State == CodingClientActivityState.WaitingForConfirmation)
        {
            steps.Add("先处理 AI 编程客户端里的权限确认，确认命令安全后再继续。");
        }
        else
        {
            steps.Add("不急着补全全部上下文，只确认一个最小可继续动作。");
        }

        steps.Add("如果这个断点对应一个计划，把结果更新到记忆星图；如果只是临时现场，就继续工作，不必强行保存。");
        return steps;
    }

    private static IReadOnlyList<string> CreateBreakpointEvidenceItems(
        BreakpointSnapshot snapshot,
        CodingClientActivitySnapshot? codingSnapshot,
        string title)
    {
        var items = new List<string>
        {
            $"窗口：{title}",
            $"进程：{snapshot.ProcessName}"
        };

        if (!string.IsNullOrWhiteSpace(snapshot.RelatedPlanTitle))
        {
            items.Add($"关联计划：{snapshot.RelatedPlanTitle}");
        }

        foreach (var line in SplitEvidenceLines(snapshot.Evidence).Take(3))
        {
            if (!items.Any(item => string.Equals(item, line, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(line);
            }
        }

        if (codingSnapshot != null && codingSnapshot.State != CodingClientActivityState.Idle)
        {
            items.Add($"AI 编程状态：{codingSnapshot.ClientName} · {FormatCodingState(codingSnapshot.State)}");
        }

        return items.Take(6).ToList();
    }

    private static IEnumerable<string> SplitEvidenceLines(string evidence)
    {
        return evidence
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static string CreateBreakpointCapsuleIntro(BreakpointSnapshot snapshot, string title)
    {
        var plan = string.IsNullOrWhiteSpace(snapshot.RelatedPlanTitle)
            ? "没有强行匹配到计划"
            : $"关联「{snapshot.RelatedPlanTitle}」";
        return $"你离开前停在「{title}」，{plan}。这颗胶囊只保留恢复现场最有用的线索。";
    }

    private static string CreateBreakpointRecoveryPrompt(BreakpointSnapshot snapshot, string title)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.RelatedPlanTitle))
        {
            return $"恢复口令：继续「{snapshot.RelatedPlanTitle}」，我离开前停在「{title}」，先帮我找回下一步。";
        }

        return $"恢复口令：我刚才停在「{title}」，先帮我用 3 分钟找回现场和下一步。";
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
