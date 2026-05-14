using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
    private readonly DatabaseService _dbService;
    private readonly SettingsService _settingsService;
    private readonly ProcessMonitorService _processMonitor;
    private readonly FocusClassificationClient? _focusClient;
    private readonly ContextRetrievalService _contextRetrievalService;
    private readonly MemoryExtractionService _memoryExtractionService;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;

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
    [NotifyCanExecuteChangedFor(nameof(SendConversationMessageCommand))]
    private string _conversationInput = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ConversationMessage> _conversationMessages = new();

    [ObservableProperty]
    private ObservableCollection<FocusTask> _focusTasks = new();

    [ObservableProperty]
    private ObservableCollection<ContextMemory> _contextMemories = new();

    [ObservableProperty]
    private ObservableCollection<GalaxyLinkViewModel> _galaxyLinks = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGalaxyTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteGalaxyTaskCommand))]
    private ContextMemory? _selectedGalaxyMemory;

    [ObservableProperty]
    private string _galaxyEditTitle = string.Empty;

    [ObservableProperty]
    private string _galaxyEditCreatedAtText = string.Empty;

    [ObservableProperty]
    private bool _galaxyEditIsCompleted;

    [ObservableProperty]
    private string _currentFocusGoal = string.Empty;

    [ObservableProperty]
    private bool _isGalaxyVisible;

    [ObservableProperty]
    private bool _hasConversationStarted;

    public int ApplicationCount => Applications.Count;
    public int ProductiveCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Productive);
    public int DistractingCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Distracting);
    public int UnknownCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Unknown);
    public string TotalDurationText => FormatDuration(Applications.Aggregate(TimeSpan.Zero, (total, item) => total + item.TotalDuration));
    public string RecentSessionCountText => RecentSessions.Count.ToString();
    public bool IsMonitorEnabled => _settingsService.Settings.MonitorEnabled;
    public string MonitorButtonText => IsMonitorEnabled ? T("Main_PauseMonitor") : T("Main_StartMonitor");
    public string MonitoringStateText => IsMonitorEnabled ? T("Main_MonitoringOn") : T("Main_MonitoringOff");
    public string CurrentFocusGoalDisplay => string.IsNullOrWhiteSpace(CurrentFocusGoal)
        ? T("Main_NoTask")
        : CurrentFocusGoal;
    public string ApplicationCountText => string.Format(T("Main_AppsCountFormat"), ApplicationCount);
    public string FocusTaskCountText => string.Format(T("Main_FocusTaskCountFormat"), FocusTasks.Count(t => t.Status == FocusTaskStatus.Completed), FocusTasks.Count);
    public string MemoryCountText => string.Format(T("Main_MemoryCountFormat"), ContextMemories.Count);
    public bool HasSelectedGalaxyTask => SelectedGalaxyMemory != null;
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
        Action openSettings,
        Action exitApplication)
    {
        _dbService = dbService;
        _settingsService = settingsService;
        _processMonitor = processMonitor;
        _focusClient = focusClient;
        _contextRetrievalService = contextRetrievalService;
        _memoryExtractionService = memoryExtractionService;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _processMonitor.ForegroundFocusUpdated += OnForegroundFocusUpdated;
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
    }

    [RelayCommand]
    private async Task GenerateDailyReview()
    {
        HasConversationStarted = true;
        StatusText = T("Main_DailyReviewGenerating");
        var snapshot = _processMonitor.SampleForegroundWindowFocus();
        var memories = await _contextRetrievalService.RetrieveAsync("today local memory context recap", snapshot, 10);
        PersonalizedReplyResult? reply = null;
        if (_focusClient?.IsConfigured == true)
        {
            try
            {
                reply = await _focusClient.CreatePersonalizedReplyAsync(
                    "请根据本地记忆、当前桌面上下文和最近应用使用情况，生成一段简短的上下文复盘。不要评价专注程度，只总结最近推进了什么、偏好有什么变化、下次回来可以从哪里继续。",
                    ContextRetrievalService.BuildContextPack(memories),
                    snapshot);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        ConversationMessages.Add(ConversationMessage.Assistant(
            !string.IsNullOrWhiteSpace(reply?.Reply)
                ? reply.Reply.Trim()
                : CreateLocalContextReply(memories, snapshot)));
        StatusText = T("Main_DailyReviewReady");
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
            SelectedGalaxyMemory.Tags,
            GalaxyEditIsCompleted ? Math.Max(SelectedGalaxyMemory.Weight, 0.85) : Math.Min(SelectedGalaxyMemory.Weight, 0.75),
            SelectedGalaxyMemory.Id);

        StatusText = T("Main_GalaxyTaskSaved");
        await RefreshContextMemoriesAsync(updated.Id);
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

    private bool CanEditGalaxyTask()
    {
        return SelectedGalaxyMemory != null;
    }

    private bool CanSendConversationMessage()
    {
        return !string.IsNullOrWhiteSpace(ConversationInput);
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

    private async Task DispatchContextAssistantAsync(string text)
    {
        StatusText = T("Main_ContextRetrieving");
        var snapshot = _processMonitor.SampleForegroundWindowFocus();
        var memories = await _contextRetrievalService.RetrieveAsync(text, snapshot);
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
                reply = await _focusClient.CreatePersonalizedReplyAsync(text, contextPack, snapshot);
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
            !string.IsNullOrWhiteSpace(candidate.Content))
        {
            await _dbService.UpsertContextMemoryAsync(
                candidate.Title,
                candidate.Content,
                MemoryExtractionService.ParseType(candidate.Type),
                "ai-candidate",
                string.Join(", ", candidate.Tags.Take(8)),
                candidate.Weight);
            await RefreshContextMemoriesAsync();
        }
        else
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
        ContextMemories = new ObservableCollection<ContextMemory>(await _dbService.GetContextMemoriesAsync());
        GalaxyLinks = new ObservableCollection<GalaxyLinkViewModel>(CreateMemoryGalaxyLinks(ContextMemories));
        SelectedGalaxyMemory = highlightId.HasValue
            ? ContextMemories.FirstOrDefault(memory => memory.Id == highlightId.Value)
            : SelectedGalaxyMemory == null
                ? null
                : ContextMemories.FirstOrDefault(memory => memory.Id == SelectedGalaxyMemory.Id);
        OnPropertyChanged(nameof(MemoryCountText));

        if (highlightId.HasValue)
        {
            StatusText = string.Format(T("Main_MemorySaved"), highlightId.Value);
        }
    }

    private static IEnumerable<GalaxyLinkViewModel> CreateMemoryGalaxyLinks(IReadOnlyCollection<ContextMemory> memories)
    {
        var ordered = memories.OrderBy(memory => memory.CreatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var source = ordered[i];
                var target = ordered[j];
                var strength = CalculateMemoryLinkStrength(source, target);
                if (strength <= 0)
                {
                    continue;
                }

                yield return new GalaxyLinkViewModel(
                    source.X + source.NodeSize / 2,
                    source.Y + source.NodeSize / 2,
                    target.X + target.NodeSize / 2,
                    target.Y + target.NodeSize / 2,
                    strength);
            }
        }
    }

    private static double CalculateMemoryLinkStrength(ContextMemory source, ContextMemory target)
    {
        var sameConstellation = !string.IsNullOrWhiteSpace(source.ConstellationName) &&
            string.Equals(source.ConstellationName, target.ConstellationName, StringComparison.OrdinalIgnoreCase);
        var sameType = source.Type == target.Type;
        var sourceTags = SplitTags(source.Tags);
        var targetTags = SplitTags(target.Tags);
        var overlap = sourceTags.Intersect(targetTags, StringComparer.OrdinalIgnoreCase).Count();

        if (!sameConstellation && !sameType && overlap == 0)
        {
            return 0;
        }

        return Math.Clamp((sameConstellation ? 0.36 : 0) + (sameType ? 0.18 : 0) + overlap * 0.16, 0.22, 0.92);
    }

    private static HashSet<string> SplitTags(string tags)
    {
        return tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<GalaxyLinkViewModel> CreateGalaxyLinks(
        IReadOnlyCollection<FocusTask> tasks,
        IEnumerable<FocusTaskLink> links)
    {
        var byId = tasks.ToDictionary(t => t.Id);
        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.SourceTaskId, out var source) ||
                !byId.TryGetValue(link.TargetTaskId, out var target))
            {
                continue;
            }

            yield return new GalaxyLinkViewModel(
                source.X + source.NodeSize / 2,
                source.Y + source.NodeSize / 2,
                target.X + target.NodeSize / 2,
                target.Y + target.NodeSize / 2,
                link.Strength);
        }
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
            SuggestedNextAction = todayTasks
                .Where(task => task.Status == FocusTaskStatus.Active && !string.IsNullOrWhiteSpace(task.NextAction))
                .Select(task => task.NextAction)
                .FirstOrDefault() ?? T("Main_DailyReviewFallbackNextAction")
        };
    }

    private static string FormatDailyReview(DailyReviewDraft review)
    {
        var highlights = review.Highlights.Count == 0
            ? string.Empty
            : "\n" + T("Main_DailyReviewHighlights") + "\n" + string.Join("\n", review.Highlights.Select(item => $"• {item}"));
        var risks = review.Risks.Count == 0
            ? string.Empty
            : "\n" + T("Main_DailyReviewRisks") + "\n" + string.Join("\n", review.Risks.Select(item => $"• {item}"));
        var next = string.IsNullOrWhiteSpace(review.SuggestedNextAction)
            ? string.Empty
            : "\n" + T("Main_DailyReviewNextAction") + "\n" + review.SuggestedNextAction.Trim();

        return $"{review.Review.Trim()}{highlights}{risks}{next}";
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

    private void SeedConversation()
    {
        ConversationMessages.Add(ConversationMessage.Assistant(T("Main_AssistantSeed")));

        if (!string.IsNullOrWhiteSpace(CurrentFocusGoal))
        {
            ConversationMessages.Add(ConversationMessage.Assistant(
                string.Format(T("Main_AssistantCurrentTaskFormat"), CurrentFocusGoal)));
        }
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

        var top = memories.Take(3).Select(memory => $"• {memory.Title}: {memory.Preview}");
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
    }

    partial void OnContextMemoriesChanged(ObservableCollection<ContextMemory> value)
    {
        OnPropertyChanged(nameof(MemoryCountText));
    }

    partial void OnSelectedGalaxyMemoryChanged(ContextMemory? value)
    {
        OnPropertyChanged(nameof(HasSelectedGalaxyTask));

        if (value == null)
        {
            GalaxyEditTitle = string.Empty;
            GalaxyEditCreatedAtText = string.Empty;
            GalaxyEditIsCompleted = false;
            return;
        }

        GalaxyEditTitle = value.Title;
        GalaxyEditCreatedAtText = value.UpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        GalaxyEditIsCompleted = value.Weight >= 0.8;
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

public partial class ConversationMessage : ObservableObject
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(14);

    private ConversationMessage(string text, bool isUser)
    {
        _text = isUser ? text : string.Empty;
        IsUser = isUser;
        Timestamp = DateTime.Now;
        Alignment = isUser ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;

        if (!isUser)
        {
            _ = StreamTextAsync(text);
        }
    }

    [ObservableProperty]
    private string _text;

    public bool IsUser { get; }
    public DateTime Timestamp { get; }
    public System.Windows.HorizontalAlignment Alignment { get; }

    public static ConversationMessage User(string text) => new(text, true);
    public static ConversationMessage Assistant(string text) => new(text, false);

    private async Task StreamTextAsync(string text)
    {
        foreach (var character in text)
        {
            Text += character;
            await Task.Delay(TypingDelay);
        }
    }
}

public sealed class GalaxyLinkViewModel(double x1, double y1, double x2, double y2, double strength)
{
    public double X1 { get; } = x1;
    public double Y1 { get; } = y1;
    public double X2 { get; } = x2;
    public double Y2 { get; } = y2;
    public double Opacity { get; } = Math.Clamp(strength, 0.25, 0.85);
}
