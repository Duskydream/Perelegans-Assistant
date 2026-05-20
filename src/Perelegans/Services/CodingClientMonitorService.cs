using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Perelegans.Models;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Perelegans.Services;

public enum CodingClientKind
{
    CodexDesktop,
    ClaudeDesktop,
    OpenCodeDesktop
}

public enum CodingClientActivityState
{
    Idle,
    Coding,
    WaitingForConfirmation,
    Completed
}

public sealed record CodingClientActivitySnapshot(
    CodingClientKind ClientKind,
    CodingClientActivityState State,
    string ClientName,
    string Message,
    string WorkspaceRoot,
    string LastChangedPath,
    DateTime UpdatedAt);

public sealed class CodingClientMonitorService : IDisposable
{
    private enum CodexLifecycleEventKind
    {
        None,
        Started,
        Active,
        Completed
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CodexSignalWindow = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan WorkspaceWriteWindow = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan CompletionVisibleWindow = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan CompletionSettledDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NotificationWaitingVisibleWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CodexApprovalReviewSuppressionWindow = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan WorkspaceRefreshInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CodexLifecycleActiveWindow = TimeSpan.FromHours(2);
    private static readonly TimeSpan ClaudeLifecycleActiveWindow = TimeSpan.FromHours(2);
    private static readonly TimeSpan OpenCodeLifecycleActiveWindow = TimeSpan.FromHours(2);
    private const int CodexRolloutTailLineCount = 1000;
    private const int CodexRecentRolloutFileLimit = 4;
    private const int ClaudeMainLogTailLineCount = 1000;

    private static readonly string[] CodexSignalFileNames =
    [
        ".codex-global-state.json",
        "session_index.jsonl"
    ];

    private static readonly string[] CodexSignalFilePatterns =
    [
        "logs_*.sqlite",
        "logs_*.sqlite-wal",
        "state_*.sqlite",
        "state_*.sqlite-wal"
    ];

    private static readonly string[] IgnoredPathSegments =
    [
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.codex{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.idea{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.next{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.nuxt{Path.DirectorySeparatorChar}"
    ];

    private static readonly HashSet<string> InterestingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".xaml",
        ".csproj",
        ".sln",
        ".slnx",
        ".props",
        ".targets",
        ".json",
        ".jsonl",
        ".toml",
        ".xml",
        ".resx",
        ".md",
        ".txt",
        ".yml",
        ".yaml",
        ".ts",
        ".tsx",
        ".js",
        ".jsx",
        ".mjs",
        ".cjs",
        ".css",
        ".scss",
        ".html",
        ".vue",
        ".py",
        ".rs",
        ".go",
        ".java",
        ".kt",
        ".cpp",
        ".c",
        ".h",
        ".hpp",
        ".fs",
        ".fsproj",
        ".sql",
        ".ps1",
        ".sh"
    };

    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _codexSignalWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _claudeSignalWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _openCodeSignalWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _workspaceWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _workspaceRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _codexHome;
    private readonly string _claudeRoamingDir;
    private readonly string _claudeLocalDir;
    private readonly string _claudeLocal3pDir;
    private readonly string _openCodeRoamingDir;
    private readonly string _openCodeLocalDir;
    private CodingClientActivitySnapshot _currentSnapshot = CreateIdleSnapshot(DateTime.Now);
    private DateTime _lastCodexSignalAt = DateTime.MinValue;
    private DateTime _lastClaudeSignalAt = DateTime.MinValue;
    private DateTime _lastOpenCodeSignalAt = DateTime.MinValue;
    private DateTime _lastWorkspaceChangeAt = DateTime.MinValue;
    private DateTime _lastWorkspaceRefreshAt = DateTime.MinValue;
    private DateTime _lastNotificationStateAt = DateTime.MinValue;
    private DateTime _lastCodexLifecycleEventAt = DateTime.MinValue;
    private DateTime _lastCodexLifecycleCompletedAt = DateTime.MinValue;
    private DateTime _lastCodexApprovalReviewAt = DateTime.MinValue;
    private DateTime _lastClaudeLifecycleEventAt = DateTime.MinValue;
    private DateTime _lastClaudeLifecycleCompletedAt = DateTime.MinValue;
    private DateTime _lastOpenCodeLifecycleEventAt = DateTime.MinValue;
    private DateTime _lastOpenCodeLifecycleCompletedAt = DateTime.MinValue;
    private CodingClientKind _lastNotificationClientKind = CodingClientKind.CodexDesktop;
    private CodingClientActivityState _lastNotificationState = CodingClientActivityState.Idle;
    private UserNotificationListener? _notificationListener;
    private string _lastNotificationMessage = string.Empty;
    private string _lastChangedPath = string.Empty;
    private string _lastCodexLifecycleSignature = string.Empty;
    private string _activeCodexLifecyclePath = string.Empty;
    private string _lastClaudeLifecycleSignature = string.Empty;
    private string _lastOpenCodeLifecycleSignature = string.Empty;
    private readonly HashSet<uint> _processedNotificationIds = [];
    private bool _isCodexLifecycleRunning;
    private bool _isClaudeLifecycleRunning;
    private bool _isOpenCodeLifecycleRunning;
    private bool _disposed;

    public event Action<CodingClientActivitySnapshot>? ActivityChanged;

    public CodingClientActivitySnapshot CurrentSnapshot => _currentSnapshot;

    public CodingClientMonitorService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _codexHome = ResolveCodexHome();
        _claudeRoamingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude");
        _claudeLocalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Claude");
        _claudeLocal3pDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Claude-3p");
        _openCodeRoamingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ai.opencode.desktop");
        _openCodeLocalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ai.opencode.desktop");
        _timer = new DispatcherTimer
        {
            Interval = PollInterval
        };
        _timer.Tick += OnTimerTick;
    }

    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        UpdateCodexSignalFiles(DateTime.Now, baselineOnly: true);
        UpdateClaudeSignalFiles(DateTime.Now, baselineOnly: true);
        UpdateOpenCodeSignalFiles(DateTime.Now, baselineOnly: true);
        UpdateOpenCodeLifecycleState(DateTime.Now);
        RefreshWorkspaceWatchers();
        _timer.Start();
        _ = InitializeNotificationListenerAsync();
        Publish(CreateIdleSnapshot(DateTime.Now));
    }

    public void Stop()
    {
        _timer.Stop();
        StopNotificationListener();
        Publish(CreateIdleSnapshot(DateTime.Now));
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            TickMultiClient();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Coding client monitor error: {ex.Message}");
        }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        if (!_settingsService.Settings.CodingClientMonitorEnabled ||
            !_settingsService.Settings.CodexDesktopMonitorEnabled)
        {
            Publish(CreateIdleSnapshot(now));
            return;
        }

        var hasCodexSignal = UpdateCodexSignalFiles(now, baselineOnly: false);
        var hasCodexLifecycleActivity = UpdateCodexLifecycleState(now);
        if (hasCodexSignal || now - _lastWorkspaceRefreshAt >= WorkspaceRefreshInterval)
        {
            RefreshWorkspaceWatchers();
        }

        DateTime lastWorkspaceChangeAt;
        string lastChangedPath;
        lock (_gate)
        {
            lastWorkspaceChangeAt = _lastWorkspaceChangeAt;
            lastChangedPath = _lastChangedPath;
        }

        if (TryGetActiveNotificationState(
                now,
                lastChangedPath,
                hasCodexLifecycleActivity,
                hasClaudeLifecycleActivity: false,
                hasOpenCodeLifecycleActivity: false,
                lastWorkspaceChangeAt,
                out var notificationSnapshot))
        {
            Publish(notificationSnapshot);
            return;
        }

        if (hasCodexLifecycleActivity)
        {
            Publish(CreateSnapshot(
                CodingClientActivityState.Coding,
                now,
                "Codex Desktop 正在写代码，我先陪它敲一会儿。",
                lastChangedPath));
            return;
        }

        if (HasRecentApprovalReview(CodingClientKind.CodexDesktop, now))
        {
            Publish(CreateSnapshot(
                CodingClientActivityState.WaitingForConfirmation,
                now,
                "Codex Desktop 正在处理权限确认，还不能算生成完成。",
                lastChangedPath));
            return;
        }

        if (IsCompletionSettled(now, _lastCodexLifecycleCompletedAt))
        {
            Publish(CreateSnapshot(
                CodingClientActivityState.Completed,
                now,
                "Codex Desktop 刚完成生成，去看看吧。",
                lastChangedPath));
            return;
        }

        Publish(CreateIdleSnapshot(now));
    }

    private void TickMultiClient()
    {
        var now = DateTime.Now;
        if (!_settingsService.Settings.CodingClientMonitorEnabled ||
            (!_settingsService.Settings.CodexDesktopMonitorEnabled &&
             !_settingsService.Settings.ClaudeDesktopMonitorEnabled &&
             !_settingsService.Settings.OpenCodeDesktopMonitorEnabled))
        {
            Publish(CreateIdleSnapshot(now));
            return;
        }

        var codexEnabled = _settingsService.Settings.CodexDesktopMonitorEnabled;
        var claudeEnabled = _settingsService.Settings.ClaudeDesktopMonitorEnabled;
        var openCodeEnabled = _settingsService.Settings.OpenCodeDesktopMonitorEnabled;
        var hasCodexSignal = codexEnabled && UpdateCodexSignalFiles(now, baselineOnly: false);
        var hasCodexLifecycleActivity = codexEnabled && UpdateCodexLifecycleState(now);
        var hasClaudeSignal = claudeEnabled && UpdateClaudeSignalFiles(now, baselineOnly: false);
        var hasClaudeLifecycleActivity = claudeEnabled && UpdateClaudeLifecycleState(now);
        var hasOpenCodeSignal = openCodeEnabled && UpdateOpenCodeSignalFiles(now, baselineOnly: false);
        var hasOpenCodeLifecycleActivity = openCodeEnabled && UpdateOpenCodeLifecycleState(now);
        if (hasCodexSignal || hasOpenCodeSignal || now - _lastWorkspaceRefreshAt >= WorkspaceRefreshInterval)
        {
            RefreshWorkspaceWatchers();
        }

        string lastChangedPath;
        DateTime lastWorkspaceChangeAt;
        lock (_gate)
        {
            lastChangedPath = _lastChangedPath;
            lastWorkspaceChangeAt = _lastWorkspaceChangeAt;
        }

        if (TryGetActiveNotificationState(
                now,
                lastChangedPath,
                hasCodexLifecycleActivity,
                hasClaudeLifecycleActivity,
                hasOpenCodeLifecycleActivity,
                lastWorkspaceChangeAt,
                out var notificationSnapshot))
        {
            Publish(notificationSnapshot);
            return;
        }

        var hasRecentOpenCodeSignal = now - _lastOpenCodeSignalAt <= CodexSignalWindow;
        var hasRecentWorkspaceChange = now - lastWorkspaceChangeAt <= WorkspaceWriteWindow;
        if (codexEnabled &&
            hasCodexLifecycleActivity)
        {
            Publish(CreateSnapshot(
                CodingClientKind.CodexDesktop,
                CodingClientActivityState.Coding,
                now,
                "Codex Desktop 正在写代码，我先陪它敲一会儿。",
                lastChangedPath));
            return;
        }

        if (claudeEnabled &&
            hasClaudeLifecycleActivity)
        {
            Publish(CreateSnapshot(
                CodingClientKind.ClaudeDesktop,
                CodingClientActivityState.Coding,
                now,
                "Claude Desktop 正在生成回复，我先陪它想一会儿。",
                lastChangedPath));
            return;
        }

        if (openCodeEnabled &&
            (hasOpenCodeLifecycleActivity ||
             (IsOpenCodeProcessRunning() && hasRecentWorkspaceChange && hasRecentOpenCodeSignal)))
        {
            Publish(CreateSnapshot(
                CodingClientKind.OpenCodeDesktop,
                CodingClientActivityState.Coding,
                now,
                "OpenCode 正在改代码，我先陪它跑一会儿。",
                lastChangedPath));
            return;
        }

        if (codexEnabled &&
            HasRecentApprovalReview(CodingClientKind.CodexDesktop, now))
        {
            Publish(CreateSnapshot(
                CodingClientKind.CodexDesktop,
                CodingClientActivityState.WaitingForConfirmation,
                now,
                "Codex Desktop 正在处理权限确认，还不能算生成完成。",
                lastChangedPath));
            return;
        }

        if (codexEnabled &&
            IsCompletionSettled(now, _lastCodexLifecycleCompletedAt))
        {
            Publish(CreateSnapshot(
                CodingClientKind.CodexDesktop,
                CodingClientActivityState.Completed,
                now,
                "Codex Desktop 刚完成生成，去看看吧。",
                lastChangedPath));
            return;
        }

        if (claudeEnabled &&
            IsCompletionSettled(now, _lastClaudeLifecycleCompletedAt))
        {
            Publish(CreateSnapshot(
                CodingClientKind.ClaudeDesktop,
                CodingClientActivityState.Completed,
                now,
                "Claude Desktop 刚完成回复，去看看吧。",
                lastChangedPath));
            return;
        }

        if (openCodeEnabled &&
            IsCompletionSettled(now, _lastOpenCodeLifecycleCompletedAt))
        {
            Publish(CreateSnapshot(
                CodingClientKind.OpenCodeDesktop,
                CodingClientActivityState.Completed,
                now,
                "OpenCode 刚完成生成，去看看结果吧。",
                lastChangedPath));
            return;
        }

        Publish(CreateIdleSnapshot(now));
    }

    private async Task InitializeNotificationListenerAsync()
    {
        if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
        {
            return;
        }

        try
        {
            var listener = UserNotificationListener.Current;
            var status = listener.GetAccessStatus();
            if (status == UserNotificationListenerAccessStatus.Unspecified)
            {
                status = await listener.RequestAccessAsync();
            }

            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                return;
            }

            _notificationListener = listener;
            _notificationListener.NotificationChanged += OnNotificationChanged;
            await BaselineExistingNotificationsAsync(listener);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Windows notification listener unavailable: {ex.Message}");
        }
    }

    private void StopNotificationListener()
    {
        if (_notificationListener == null)
        {
            return;
        }

        try
        {
            _notificationListener.NotificationChanged -= OnNotificationChanged;
        }
        catch
        {
            // Ignore listener teardown races during application shutdown.
        }

        _notificationListener = null;
    }

    private async Task BaselineExistingNotificationsAsync(UserNotificationListener listener)
    {
        try
        {
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            lock (_gate)
            {
                foreach (var notification in notifications)
                {
                    _processedNotificationIds.Add(notification.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to baseline Windows notifications: {ex.Message}");
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added)
        {
            return;
        }

        try
        {
            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification != null)
            {
                ProcessNotification(notification);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to process Windows notification: {ex.Message}");
        }
    }

    private void ProcessNotification(UserNotification notification)
    {
        if (!_settingsService.Settings.CodingClientMonitorEnabled ||
            (!_settingsService.Settings.CodexDesktopMonitorEnabled &&
             !_settingsService.Settings.ClaudeDesktopMonitorEnabled &&
             !_settingsService.Settings.OpenCodeDesktopMonitorEnabled))
        {
            return;
        }

        lock (_gate)
        {
            if (!_processedNotificationIds.Add(notification.Id))
            {
                return;
            }
        }

        var appName = GetNotificationAppName(notification);
        var text = GetNotificationText(notification);
        var combinedText = $"{appName}\n{text}";
        if (!TryGetNotificationClientKind(appName, out var clientKind))
        {
            return;
        }

        if ((clientKind == CodingClientKind.CodexDesktop &&
             !_settingsService.Settings.CodexDesktopMonitorEnabled) ||
            (clientKind == CodingClientKind.ClaudeDesktop &&
             !_settingsService.Settings.ClaudeDesktopMonitorEnabled) ||
            (clientKind == CodingClientKind.OpenCodeDesktop &&
             !_settingsService.Settings.OpenCodeDesktopMonitorEnabled))
        {
            return;
        }

        if (IsCompletionNotification(clientKind, combinedText))
        {
            if (clientKind == CodingClientKind.CodexDesktop)
            {
                lock (_gate)
                {
                    _lastCodexApprovalReviewAt = DateTime.MinValue;
                }
            }

            MarkClientState(
                clientKind,
                CodingClientActivityState.Completed,
                $"{GetClientName(clientKind)} completed generation; please review it.");
            return;
        }

        if (IsApprovalNotification(combinedText))
        {
            if (clientKind == CodingClientKind.CodexDesktop)
            {
                lock (_gate)
                {
                    _lastCodexApprovalReviewAt = DateTime.Now;
                }
            }

            MarkClientState(
                clientKind,
                CodingClientActivityState.WaitingForConfirmation,
                $"{GetClientName(clientKind)} 在等你确认。");
            return;
        }

    }

    private static bool TryGetNotificationClientKind(string appName, out CodingClientKind kind)
    {
        if (ContainsAny(appName, "opencode", "open code", "ai.opencode"))
        {
            kind = CodingClientKind.OpenCodeDesktop;
            return true;
        }

        if (ContainsAny(appName, "claude", "anthropic"))
        {
            kind = CodingClientKind.ClaudeDesktop;
            return true;
        }

        if (ContainsAny(appName, "codex"))
        {
            kind = CodingClientKind.CodexDesktop;
            return true;
        }

        kind = CodingClientKind.CodexDesktop;
        return false;
    }

    private void MarkClientState(CodingClientKind kind, CodingClientActivityState state, string message)
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            _lastNotificationClientKind = kind;
            _lastNotificationState = state;
            _lastNotificationMessage = message;
            _lastNotificationStateAt = now;
        }

        _ = _timer.Dispatcher.InvokeAsync(() =>
        {
            string lastChangedPath;
            lock (_gate)
            {
                lastChangedPath = _lastChangedPath;
            }

            Publish(CreateSnapshot(kind, state, DateTime.Now, message, lastChangedPath));
        });
    }

    private bool TryGetActiveNotificationState(
        DateTime now,
        string lastChangedPath,
        bool hasCodexLifecycleActivity,
        bool hasClaudeLifecycleActivity,
        bool hasOpenCodeLifecycleActivity,
        DateTime lastWorkspaceChangeAt,
        out CodingClientActivitySnapshot snapshot)
    {
        snapshot = CreateIdleSnapshot(now);

        CodingClientActivityState state;
        CodingClientKind kind;
        DateTime updatedAt;
        string message;
        lock (_gate)
        {
            kind = _lastNotificationClientKind;
            state = _lastNotificationState;
            updatedAt = _lastNotificationStateAt;
            message = _lastNotificationMessage;
        }

        if (state == CodingClientActivityState.Idle)
        {
            return false;
        }

        if (state == CodingClientActivityState.Completed)
        {
            if (HasRecentApprovalReview(kind, now))
            {
                return false;
            }

            if (!IsCompletionSettled(now, updatedAt) ||
                IsClientCurrentlyCoding(
                    kind,
                    now,
                    hasCodexLifecycleActivity,
                    hasClaudeLifecycleActivity,
                    hasOpenCodeLifecycleActivity,
                    lastWorkspaceChangeAt) ||
                HasClientActivityAfter(kind, updatedAt, lastWorkspaceChangeAt))
            {
                return false;
            }
        }
        else if (state == CodingClientActivityState.WaitingForConfirmation)
        {
            var completedAt = kind == CodingClientKind.CodexDesktop
                ? _lastCodexLifecycleCompletedAt
                : kind == CodingClientKind.ClaudeDesktop
                    ? _lastClaudeLifecycleCompletedAt
                    : _lastOpenCodeLifecycleCompletedAt;
            if (completedAt > updatedAt &&
                IsCompletionSettled(now, completedAt))
            {
                return false;
            }

            if (now - updatedAt > NotificationWaitingVisibleWindow)
            {
                return false;
            }
        }
        else if (now - updatedAt > NotificationWaitingVisibleWindow)
        {
            return false;
        }

        snapshot = CreateSnapshot(kind, state, now, message, lastChangedPath);
        return true;
    }

    private static bool IsCompletionSettled(DateTime now, DateTime completedAt)
    {
        return completedAt != DateTime.MinValue &&
               now - completedAt >= CompletionSettledDelay &&
               now - completedAt <= CompletionVisibleWindow;
    }

    private bool HasRecentApprovalReview(CodingClientKind kind, DateTime now)
    {
        return kind == CodingClientKind.CodexDesktop &&
               _lastCodexApprovalReviewAt != DateTime.MinValue &&
               !(_lastCodexLifecycleCompletedAt != DateTime.MinValue &&
                 now - _lastCodexLifecycleCompletedAt <= CompletionVisibleWindow) &&
               now - _lastCodexApprovalReviewAt <= CodexApprovalReviewSuppressionWindow;
    }

    private bool IsClientCurrentlyCoding(
        CodingClientKind kind,
        DateTime now,
        bool hasCodexLifecycleActivity,
        bool hasClaudeLifecycleActivity,
        bool hasOpenCodeLifecycleActivity,
        DateTime lastWorkspaceChangeAt)
    {
        var hasLifecycleActivity = kind switch
        {
            CodingClientKind.CodexDesktop => hasCodexLifecycleActivity,
            CodingClientKind.ClaudeDesktop => hasClaudeLifecycleActivity,
            CodingClientKind.OpenCodeDesktop => hasOpenCodeLifecycleActivity,
            _ => false
        };
        if (kind != CodingClientKind.OpenCodeDesktop)
        {
            return hasLifecycleActivity;
        }

        return hasLifecycleActivity ||
               (now - _lastOpenCodeSignalAt <= CodexSignalWindow &&
                now - lastWorkspaceChangeAt <= WorkspaceWriteWindow &&
                IsOpenCodeProcessRunning());
    }

    private bool HasClientActivityAfter(
        CodingClientKind kind,
        DateTime timestamp,
        DateTime lastWorkspaceChangeAt)
    {
        var lastSignalAt = kind switch
        {
            CodingClientKind.CodexDesktop => _lastCodexSignalAt,
            CodingClientKind.ClaudeDesktop => _lastClaudeSignalAt,
            CodingClientKind.OpenCodeDesktop => _lastOpenCodeSignalAt,
            _ => DateTime.MinValue
        };

        return lastSignalAt > timestamp.AddMilliseconds(500) ||
               lastWorkspaceChangeAt > timestamp.AddMilliseconds(500);
    }

    private bool UpdateCodexSignalFiles(DateTime now, bool baselineOnly)
    {
        if (!Directory.Exists(_codexHome))
        {
            return false;
        }

        var changed = false;
        foreach (var path in EnumerateCodexSignalFiles())
        {
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                continue;
            }

            if (!_codexSignalWriteTimes.TryGetValue(path, out var previousWriteUtc))
            {
                _codexSignalWriteTimes[path] = lastWriteUtc;
                continue;
            }

            if (lastWriteUtc <= previousWriteUtc.AddMilliseconds(500))
            {
                continue;
            }

            _codexSignalWriteTimes[path] = lastWriteUtc;
            changed = true;
        }

        if (changed && !baselineOnly)
        {
            _lastCodexSignalAt = now;
        }

        return changed && !baselineOnly;
    }

    private IEnumerable<string> EnumerateCodexSignalFiles()
    {
        foreach (var fileName in CodexSignalFileNames)
        {
            var path = Path.Combine(_codexHome, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (var pattern in CodexSignalFilePatterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_codexHome, pattern, SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private bool UpdateCodexLifecycleState(DateTime now)
    {
        var hasLifecycleEvent = TryGetLatestCodexLifecycleEvent(
            out var isRunning,
            out var eventAt,
            out var signature,
            out var sourcePath,
            out var approvalReviewAt);

        if (approvalReviewAt > _lastCodexApprovalReviewAt)
        {
            _lastCodexApprovalReviewAt = approvalReviewAt;
            _lastCodexLifecycleCompletedAt = DateTime.MinValue;
        }

        if (!hasLifecycleEvent)
        {
            return _isCodexLifecycleRunning &&
                   now - _lastCodexLifecycleEventAt <= CodexLifecycleActiveWindow;
        }

        if (!string.Equals(signature, _lastCodexLifecycleSignature, StringComparison.Ordinal))
        {
            var wasRunning = _isCodexLifecycleRunning;
            var previousActivePath = _activeCodexLifecyclePath;

            if (!isRunning &&
                wasRunning &&
                !PathsEqual(sourcePath, previousActivePath))
            {
                return now - _lastCodexLifecycleEventAt <= CodexLifecycleActiveWindow;
            }

            _isCodexLifecycleRunning = isRunning;
            _lastCodexLifecycleEventAt = eventAt;
            _lastCodexLifecycleSignature = signature;

            if (isRunning)
            {
                _activeCodexLifecyclePath = sourcePath;
            }
            else if (wasRunning)
            {
                _lastCodexLifecycleCompletedAt = now;

                _activeCodexLifecyclePath = string.Empty;
            }
        }

        return _isCodexLifecycleRunning &&
               now - _lastCodexLifecycleEventAt <= CodexLifecycleActiveWindow;
    }

    private bool TryGetLatestCodexLifecycleEvent(
        out bool isRunning,
        out DateTime eventAt,
        out string signature,
        out string sourcePath,
        out DateTime approvalReviewAt)
    {
        isRunning = false;
        eventAt = DateTime.MinValue;
        signature = string.Empty;
        sourcePath = string.Empty;
        approvalReviewAt = DateTime.MinValue;

        foreach (var path in EnumerateRecentCodexRolloutFiles())
        {
            var fileRunning = false;
            DateTime fileEventAt = DateTime.MinValue;
            var fileSignature = string.Empty;
            var fileIsApprovalReview = false;

            foreach (var line in ReadRecentLines(path, CodexRolloutTailLineCount))
            {
                if (!TryParseCodexLifecycleLine(
                        line,
                        out var kind,
                        out var lineAt,
                        out var isApprovalReviewContext))
                {
                    continue;
                }

                if (isApprovalReviewContext)
                {
                    fileIsApprovalReview = true;
                    fileRunning = false;
                    fileEventAt = DateTime.MinValue;
                    fileSignature = string.Empty;
                    if (lineAt > approvalReviewAt)
                    {
                        approvalReviewAt = lineAt;
                    }

                    continue;
                }

                if (fileIsApprovalReview)
                {
                    continue;
                }

                switch (kind)
                {
                    case CodexLifecycleEventKind.Started:
                        fileRunning = true;
                        fileEventAt = lineAt;
                        fileSignature = $"{path}|{line}";
                        break;
                    case CodexLifecycleEventKind.Active:
                        if (fileRunning || nowIsRecent(lineAt))
                        {
                            fileRunning = true;
                            fileEventAt = lineAt;
                            fileSignature = $"{path}|{line}";
                        }

                        break;
                    case CodexLifecycleEventKind.Completed:
                        fileRunning = false;
                        fileEventAt = lineAt;
                        fileSignature = $"{path}|{line}";
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(fileSignature) || fileEventAt < eventAt)
            {
                continue;
            }

            isRunning = fileRunning;
            eventAt = fileEventAt;
            signature = fileSignature;
            sourcePath = path;
        }

        return !string.IsNullOrWhiteSpace(signature);

        bool nowIsRecent(DateTime lineAt)
        {
            return DateTime.Now - lineAt <= CodexSignalWindow;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> EnumerateRecentCodexRolloutFiles()
    {
        var sessionsDir = Path.Combine(_codexHome, "sessions");
        if (!Directory.Exists(sessionsDir))
        {
            yield break;
        }

        FileInfo[] files;
        try
        {
            files = Directory
                .EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(CodexRecentRolloutFileLimit)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file.FullName;
        }
    }

    private static bool TryParseCodexLifecycleLine(
        string line,
        out CodexLifecycleEventKind kind,
        out DateTime eventAt,
        out bool isApprovalReviewContext)
    {
        kind = CodexLifecycleEventKind.None;
        eventAt = DateTime.MinValue;
        isApprovalReviewContext = false;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryGetCodexEventTimestamp(root, out eventAt))
            {
                return false;
            }

            var rootType = TryGetStringProperty(root, "type");
            var payloadType = string.Empty;
            if (root.TryGetProperty("payload", out var payload) &&
                payload.ValueKind == JsonValueKind.Object)
            {
                payloadType = TryGetStringProperty(payload, "type");
                isApprovalReviewContext = IsCodexApprovalReviewContext(rootType, payload);
            }

            if (isApprovalReviewContext)
            {
                kind = CodexLifecycleEventKind.Active;
                return true;
            }

            if (IsCodexCompletedEvent(rootType, payloadType))
            {
                kind = CodexLifecycleEventKind.Completed;
                return true;
            }

            if (IsCodexStartedEvent(rootType, payloadType))
            {
                kind = CodexLifecycleEventKind.Started;
                return true;
            }

            if (IsCodexActiveEvent(rootType, payloadType))
            {
                kind = CodexLifecycleEventKind.Active;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryGetCodexEventTimestamp(JsonElement root, out DateTime eventAt)
    {
        eventAt = DateTime.MinValue;
        if (!root.TryGetProperty("timestamp", out var timestampElement) ||
            timestampElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var rawTimestamp = timestampElement.GetString();
        if (string.IsNullOrWhiteSpace(rawTimestamp) ||
            !DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return false;
        }

        eventAt = timestamp.LocalDateTime;
        return true;
    }

    private static bool IsCodexStartedEvent(string rootType, string payloadType)
    {
        return false;
    }

    private static bool IsCodexCompletedEvent(string rootType, string payloadType)
    {
        return payloadType.Equals("task_complete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexApprovalReviewContext(string rootType, JsonElement payload)
    {
        if (!rootType.Equals("turn_context", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var model = TryGetStringProperty(payload, "model");
        if (ContainsAny(model, "auto-review", "approval-review"))
        {
            return true;
        }

        if (payload.TryGetProperty("collaboration_mode", out var collaborationMode) &&
            collaborationMode.ValueKind == JsonValueKind.Object &&
            collaborationMode.TryGetProperty("settings", out var settings) &&
            settings.ValueKind == JsonValueKind.Object &&
            ContainsAny(TryGetStringProperty(settings, "model"), "auto-review", "approval-review"))
        {
            return true;
        }

        if (payload.TryGetProperty("final_output_json_schema", out var schema) &&
            schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty("outcome", out _))
        {
            return true;
        }

        return false;
    }

    private static bool IsCodexActiveEvent(string rootType, string payloadType)
    {
        if (rootType.Equals("response_item", StringComparison.OrdinalIgnoreCase))
        {
            return !ContainsAny(payloadType, "user_message");
        }

        return rootType.Equals("event_msg", StringComparison.OrdinalIgnoreCase) &&
               ContainsAny(
                   payloadType,
                   "agent_message",
                   "patch",
                   "tool",
                   "exec",
                   "shell",
                   "approval");
    }

    private static string TryGetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private bool UpdateClaudeLifecycleState(DateTime now)
    {
        if (!TryGetLatestClaudeLifecycleEvent(out var isRunning, out var eventAt, out var signature))
        {
            return _isClaudeLifecycleRunning &&
                   now - _lastClaudeLifecycleEventAt <= ClaudeLifecycleActiveWindow;
        }

        if (!string.Equals(signature, _lastClaudeLifecycleSignature, StringComparison.Ordinal))
        {
            var wasRunning = _isClaudeLifecycleRunning;
            _isClaudeLifecycleRunning = isRunning;
            _lastClaudeLifecycleEventAt = eventAt;
            _lastClaudeLifecycleSignature = signature;

            if (wasRunning && !isRunning)
            {
                _lastClaudeLifecycleCompletedAt = now;
            }
        }

        return _isClaudeLifecycleRunning &&
               now - _lastClaudeLifecycleEventAt <= ClaudeLifecycleActiveWindow;
    }

    private bool TryGetLatestClaudeLifecycleEvent(
        out bool isRunning,
        out DateTime eventAt,
        out string signature)
    {
        isRunning = false;
        eventAt = DateTime.MinValue;
        signature = string.Empty;

        foreach (var path in EnumerateClaudeMainLogFiles())
        {
            foreach (var line in ReadRecentLines(path, ClaudeMainLogTailLineCount))
            {
                if (!TryParseClaudeLifecycleLine(line, out var lineIsRunning, out var lineAt))
                {
                    continue;
                }

                if (lineAt < eventAt)
                {
                    continue;
                }

                isRunning = lineIsRunning;
                eventAt = lineAt;
                signature = $"{path}|{line}";
            }
        }

        return !string.IsNullOrWhiteSpace(signature);
    }

    private IEnumerable<string> EnumerateClaudeMainLogFiles()
    {
        var roamingMainLog = Path.Combine(_claudeRoamingDir, "logs", "main.log");
        if (File.Exists(roamingMainLog))
        {
            yield return roamingMainLog;
        }

        var local3pMainLog = Path.Combine(_claudeLocal3pDir, "logs", "main.log");
        if (File.Exists(local3pMainLog))
        {
            yield return local3pMainLog;
        }
    }

    private static bool TryParseClaudeLifecycleLine(string line, out bool isRunning, out DateTime eventAt)
    {
        isRunning = false;
        eventAt = DateTime.MinValue;

        if (!TryParseLogTimestamp(line, out eventAt))
        {
            return false;
        }

        if (line.Contains("[Result]", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Turn", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!line.Contains("[Lifecycle]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ContainsStateTransition(line, "running", "idle"))
        {
            return true;
        }

        if (ContainsStateTransition(line, "idle", "initializing") ||
            ContainsStateTransition(line, "initializing", "running"))
        {
            isRunning = true;
            return true;
        }

        return false;
    }

    private static bool TryParseLogTimestamp(string line, out DateTime timestamp)
    {
        timestamp = DateTime.MinValue;
        return line.Length >= 19 &&
               DateTime.TryParseExact(
                   line.AsSpan(0, 19),
                   "yyyy-MM-dd HH:mm:ss",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal,
                   out timestamp);
    }

    private static bool ContainsStateTransition(string line, string from, string to)
    {
        var fromIndex = line.IndexOf(from, StringComparison.OrdinalIgnoreCase);
        if (fromIndex < 0)
        {
            return false;
        }

        var toIndex = line.IndexOf(to, fromIndex + from.Length, StringComparison.OrdinalIgnoreCase);
        return toIndex > fromIndex;
    }

    private static string[] ReadRecentLines(string path, int maxLines)
    {
        if (maxLines <= 0 || !File.Exists(path))
        {
            return [];
        }

        Queue<string> lines = new(maxLines);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (lines.Count == maxLines)
                {
                    lines.Dequeue();
                }

                lines.Enqueue(line);
            }
        }
        catch
        {
            return [];
        }

        return lines.ToArray();
    }

    private bool UpdateClaudeSignalFiles(DateTime now, bool baselineOnly)
    {
        var changed = false;
        foreach (var path in EnumerateClaudeSignalFiles())
        {
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                continue;
            }

            if (!_claudeSignalWriteTimes.TryGetValue(path, out var previousWriteUtc))
            {
                _claudeSignalWriteTimes[path] = lastWriteUtc;
                continue;
            }

            if (lastWriteUtc <= previousWriteUtc.AddMilliseconds(500))
            {
                continue;
            }

            _claudeSignalWriteTimes[path] = lastWriteUtc;
            changed = true;
        }

        if (changed && !baselineOnly)
        {
            _lastClaudeSignalAt = now;
        }

        return changed && !baselineOnly;
    }

    private IEnumerable<string> EnumerateClaudeSignalFiles()
    {
        foreach (var path in EnumerateExistingFiles(_claudeRoamingDir, "logs", "*.log", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeRoamingDir, "Local Storage\\leveldb", "*.log", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeRoamingDir, "IndexedDB", "*.log", SearchOption.AllDirectories))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeRoamingDir, "Session Storage", "*.log", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeLocal3pDir, "logs", "*.log", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeLocal3pDir, "local-agent-mode-sessions", "*", SearchOption.AllDirectories))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeLocal3pDir, "Local Storage\\leveldb", "*.log", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeLocal3pDir, "IndexedDB", "*.log", SearchOption.AllDirectories))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_claudeLocalDir, string.Empty, "claude_desktop_config.json", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }
    }

    private bool UpdateOpenCodeSignalFiles(DateTime now, bool baselineOnly)
    {
        var changed = false;
        foreach (var path in EnumerateOpenCodeSignalFiles())
        {
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                continue;
            }

            if (!_openCodeSignalWriteTimes.TryGetValue(path, out var previousWriteUtc))
            {
                _openCodeSignalWriteTimes[path] = lastWriteUtc;
                continue;
            }

            if (lastWriteUtc <= previousWriteUtc.AddMilliseconds(500))
            {
                continue;
            }

            _openCodeSignalWriteTimes[path] = lastWriteUtc;
            changed = true;
        }

        if (changed && !baselineOnly)
        {
            _lastOpenCodeSignalAt = now;
        }

        return changed && !baselineOnly;
    }

    private IEnumerable<string> EnumerateOpenCodeSignalFiles()
    {
        foreach (var path in EnumerateExistingFiles(_openCodeRoamingDir, string.Empty, "opencode.global.dat", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_openCodeRoamingDir, string.Empty, "opencode.workspace.*.dat", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_openCodeRoamingDir, string.Empty, ".window-state.json", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in EnumerateExistingFiles(_openCodeLocalDir, "logs", "opencode-desktop_*.log", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }
    }

    private bool UpdateOpenCodeLifecycleState(DateTime now)
    {
        if (!TryGetLatestOpenCodeCompletionEvent(out var eventAt, out var signature))
        {
            return _isOpenCodeLifecycleRunning &&
                   now - _lastOpenCodeLifecycleEventAt <= OpenCodeLifecycleActiveWindow;
        }

        if (!string.Equals(signature, _lastOpenCodeLifecycleSignature, StringComparison.Ordinal))
        {
            var hadPreviousEvent = !string.IsNullOrWhiteSpace(_lastOpenCodeLifecycleSignature);
            _isOpenCodeLifecycleRunning = false;
            _lastOpenCodeLifecycleEventAt = eventAt;
            _lastOpenCodeLifecycleSignature = signature;

            if (hadPreviousEvent)
            {
                _lastOpenCodeLifecycleCompletedAt = now;
            }
        }

        return _isOpenCodeLifecycleRunning &&
               now - _lastOpenCodeLifecycleEventAt <= OpenCodeLifecycleActiveWindow;
    }

    private bool TryGetLatestOpenCodeCompletionEvent(
        out DateTime eventAt,
        out string signature)
    {
        eventAt = DateTime.MinValue;
        signature = string.Empty;

        var globalPath = Path.Combine(_openCodeRoamingDir, "opencode.global.dat");
        if (!File.Exists(globalPath) ||
            !TryReadJsonFile(globalPath, out var json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true
                });

            if (!TryGetNestedJsonString(document.RootElement, "notification", out var notificationJson))
            {
                return false;
            }

            using var notificationDocument = JsonDocument.Parse(notificationJson);
            if (!notificationDocument.RootElement.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            long latestTime = long.MinValue;
            var latestSignature = string.Empty;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryGetStringProperty(item, "type").Equals("turn-complete", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var time = item.TryGetProperty("time", out var timeElement) &&
                           timeElement.TryGetInt64(out var rawTime)
                    ? rawTime
                    : 0;
                if (time < latestTime)
                {
                    continue;
                }

                latestTime = time;
                latestSignature = string.Join(
                    "|",
                    TryGetStringProperty(item, "session"),
                    TryGetStringProperty(item, "directory"),
                    time.ToString(CultureInfo.InvariantCulture),
                    TryGetStringProperty(item, "type"));
            }

            if (string.IsNullOrWhiteSpace(latestSignature))
            {
                return false;
            }

            signature = latestSignature;
            eventAt = File.GetLastWriteTime(globalPath);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateExistingFiles(
        string root,
        string relativeDir,
        string pattern,
        SearchOption searchOption)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        var dir = string.IsNullOrWhiteSpace(relativeDir)
            ? root
            : Path.Combine(root, relativeDir);
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, pattern, searchOption).Take(200).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private void RefreshWorkspaceWatchers()
    {
        _lastWorkspaceRefreshAt = DateTime.Now;
        var roots = DiscoverCodexWorkspaceRoots()
            .Concat(DiscoverOpenCodeWorkspaceRoots())
            .Take(12)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleRoot in _workspaceRoots.Except(roots, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (_workspaceWatchers.Remove(staleRoot, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _workspaceRoots.Remove(staleRoot);
        }

        foreach (var root in roots)
        {
            if (_workspaceWatchers.ContainsKey(root))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnWorkspaceChanged;
                watcher.Created += OnWorkspaceChanged;
                watcher.Renamed += OnWorkspaceRenamed;
                watcher.Deleted += OnWorkspaceChanged;

                _workspaceWatchers[root] = watcher;
                _workspaceRoots.Add(root);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to watch coding workspace '{root}': {ex.Message}");
            }
        }
    }

    private IEnumerable<string> DiscoverCodexWorkspaceRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalStatePath = Path.Combine(_codexHome, ".codex-global-state.json");
        if (!File.Exists(globalStatePath))
        {
            return roots;
        }

        string json;
        try
        {
            json = File.ReadAllText(globalStatePath);
        }
        catch
        {
            return roots;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true
                });

            AddRootsFromJsonArray(document.RootElement, "active-workspace-roots", roots);
            AddRootsFromJsonArray(document.RootElement, "electron-saved-workspace-roots", roots);
            AddRootsFromLocalEnvironmentSelections(document.RootElement, roots);
        }
        catch (JsonException)
        {
            AddRootsFromArrayField(json, "active-workspace-roots", roots);
            AddRootsFromArrayField(json, "electron-saved-workspace-roots", roots);
        }

        return roots;
    }

    private IEnumerable<string> DiscoverOpenCodeWorkspaceRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalStatePath = Path.Combine(_openCodeRoamingDir, "opencode.global.dat");
        if (!File.Exists(globalStatePath) ||
            !TryReadJsonFile(globalStatePath, out var json))
        {
            return roots;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true
                });

            var root = document.RootElement;
            AddOpenCodeProjectRoots(root, roots);
            AddOpenCodeServerRoots(root, roots);
            AddOpenCodeLayoutRoots(root, roots);
        }
        catch (JsonException)
        {
            AddRootsFromArrayField(json, "worktree", roots);
        }

        return roots;
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        MarkWorkspaceChanged(e.FullPath);
    }

    private void OnWorkspaceRenamed(object sender, RenamedEventArgs e)
    {
        MarkWorkspaceChanged(e.FullPath);
    }

    private void MarkWorkspaceChanged(string path)
    {
        if (!IsInterestingWorkspacePath(path))
        {
            return;
        }

        lock (_gate)
        {
            _lastWorkspaceChangeAt = DateTime.Now;
            _lastChangedPath = path;
        }
    }

    private CodingClientActivitySnapshot CreateSnapshot(
        CodingClientActivityState state,
        DateTime now,
        string message,
        string lastChangedPath)
    {
        return CreateSnapshot(CodingClientKind.CodexDesktop, state, now, message, lastChangedPath);
    }

    private CodingClientActivitySnapshot CreateSnapshot(
        CodingClientKind kind,
        CodingClientActivityState state,
        DateTime now,
        string message,
        string lastChangedPath)
    {
        var workspace = FindWorkspaceForPath(lastChangedPath);
        return new CodingClientActivitySnapshot(
            kind,
            state,
            GetClientName(kind),
            message,
            kind is CodingClientKind.CodexDesktop or CodingClientKind.OpenCodeDesktop ? workspace : string.Empty,
            lastChangedPath,
            now);
    }

    private static string GetClientName(CodingClientKind kind)
    {
        return kind switch
        {
            CodingClientKind.ClaudeDesktop => "Claude Desktop",
            CodingClientKind.OpenCodeDesktop => "OpenCode",
            _ => "Codex Desktop"
        };
    }

    private string FindWorkspaceForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return _workspaceRoots.FirstOrDefault() ?? string.Empty;
        }

        return _workspaceRoots
            .OrderByDescending(root => root.Length)
            .FirstOrDefault(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private void Publish(CodingClientActivitySnapshot snapshot)
    {
        if (_currentSnapshot.ClientKind == snapshot.ClientKind &&
            _currentSnapshot.State == snapshot.State &&
            string.Equals(_currentSnapshot.WorkspaceRoot, snapshot.WorkspaceRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_currentSnapshot.LastChangedPath, snapshot.LastChangedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentSnapshot = snapshot;
        ActivityChanged?.Invoke(snapshot);
    }

    private static CodingClientActivitySnapshot CreateIdleSnapshot(DateTime now)
    {
        return new CodingClientActivitySnapshot(
            CodingClientKind.CodexDesktop,
            CodingClientActivityState.Idle,
            "Codex Desktop",
            string.Empty,
            string.Empty,
            string.Empty,
            now);
    }

    private static bool IsCodexProcessRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var name = process.ProcessName;
                    if (name.Equals("Codex", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Codex", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    string title;
                    try
                    {
                        title = process.MainWindowTitle;
                    }
                    catch
                    {
                        continue;
                    }

                    if (title.Contains("Codex", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsClaudeProcessRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var name = process.ProcessName;
                    if (name.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Claude", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    string title;
                    try
                    {
                        title = process.MainWindowTitle;
                    }
                    catch
                    {
                        continue;
                    }

                    if (title.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsOpenCodeProcessRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var name = process.ProcessName;
                    if (name.Equals("OpenCode", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("opencode-cli", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("OpenCode", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("opencode", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    string title;
                    try
                    {
                        title = process.MainWindowTitle;
                    }
                    catch
                    {
                        continue;
                    }

                    if (title.Contains("OpenCode", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("opencode", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string GetNotificationAppName(UserNotification notification)
    {
        try
        {
            return notification.AppInfo.DisplayInfo.DisplayName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetNotificationText(UserNotification notification)
    {
        try
        {
            var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding == null)
            {
                return string.Empty;
            }

            return string.Join(
                "\n",
                binding.GetTextElements()
                    .Select(element => element.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsApprovalNotification(string text)
    {
        return ContainsAny(
            text,
            "approval",
            "approval required",
            "requires approval",
            "needs approval",
            "request approval",
            "requesting approval",
            "approve",
            "approve command",
            "approve action",
            "confirm",
            "confirmation",
            "permission",
            "permission required",
            "requires permission",
            "need permission",
            "allow",
            "allow command",
            "allow action",
            "authorize",
            "authorization",
            "escalated",
            "escalation",
            "elevated",
            "sandbox",
            "outside sandbox",
            "run outside",
            "require_escalated",
            "administrator",
            "admin permission",
            "do you want to allow",
            "do you want to run",
            "needs your",
            "waiting on you",
            "requires input",
            "需要确认",
            "等待确认",
            "请求确认",
            "批准",
            "审批",
            "允许",
            "权限",
            "提权",
            "沙箱",
            "管理员",
            "等待",
            "需要你",
            "确认",
            "批准",
            "允许",
            "权限",
            "等待",
            "需要你");
    }

    private static bool IsCompletionNotification(CodingClientKind kind, string text)
    {
        if (ContainsAny(
                text,
                "running",
                "in progress",
                "generating",
                "writing",
                "executing",
                "command completed",
                "tool completed",
                "shell completed",
                "approval",
                "permission"))
        {
            return false;
        }

        if (kind == CodingClientKind.OpenCodeDesktop)
        {
            return ContainsAny(
                text,
                "opencode completed",
                "opencode finished",
                "open code completed",
                "open code finished",
                "turn complete",
                "turn-complete",
                "task complete",
                "task completed",
                "response ready",
                "ready for review",
                "finished working",
                "has completed",
                "has finished");
        }
        return kind == CodingClientKind.CodexDesktop
            ? ContainsAny(
                text,
                "codex completed",
                "codex finished",
                "codex is done",
                "task complete",
                "task completed",
                "response ready",
                "ready for review",
                "finished working",
                "has completed",
                "has finished",
                "生成完成",
                "写完",
                "写好了")
            : ContainsAny(
                text,
                "claude completed",
                "claude finished",
                "response ready",
                "ready for review",
                "finished working",
                "has completed",
                "has finished",
                "回复完成",
                "生成完成");
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveCodexHome()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, ".codex");
    }

    private static bool TryReadJsonFile(string path, out string json)
    {
        json = string.Empty;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
            return !string.IsNullOrWhiteSpace(json);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNestedJsonString(JsonElement root, string propertyName, out string json)
    {
        json = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        json = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(json);
    }

    private static void AddOpenCodeProjectRoots(JsonElement root, HashSet<string> roots)
    {
        if (!TryGetNestedJsonString(root, "globalSync.project", out var projectJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(projectJson);
            if (!document.RootElement.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var project in value.EnumerateArray())
            {
                AddRoot(TryGetStringProperty(project, "worktree"), roots);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed OpenCode state.
        }
    }

    private static void AddOpenCodeServerRoots(JsonElement root, HashSet<string> roots)
    {
        if (!TryGetNestedJsonString(root, "server", out var serverJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(serverJson);
            var server = document.RootElement;
            if (server.TryGetProperty("projects", out var projects) &&
                projects.ValueKind == JsonValueKind.Object &&
                projects.TryGetProperty("local", out var localProjects) &&
                localProjects.ValueKind == JsonValueKind.Array)
            {
                foreach (var project in localProjects.EnumerateArray())
                {
                    AddRoot(TryGetStringProperty(project, "worktree"), roots);
                }
            }

            if (server.TryGetProperty("lastProject", out var lastProject) &&
                lastProject.ValueKind == JsonValueKind.Object)
            {
                AddRoot(TryGetStringProperty(lastProject, "local"), roots);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed OpenCode state.
        }
    }

    private static void AddOpenCodeLayoutRoots(JsonElement root, HashSet<string> roots)
    {
        if (!TryGetNestedJsonString(root, "layout", out var layoutJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(layoutJson);
            AddOpenCodeRootsRecursive(document.RootElement, roots);
        }
        catch (JsonException)
        {
            // Ignore malformed OpenCode layout state.
        }
    }

    private static void AddOpenCodeRootsRecursive(JsonElement element, HashSet<string> roots)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("worktree", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("directory", StringComparison.OrdinalIgnoreCase))
                {
                    AddRoot(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null, roots);
                }

                if (LooksLikeOpenCodeLocalPath(property.Name))
                {
                    AddRoot(property.Name, roots);
                }

                AddOpenCodeRootsRecursive(property.Value, roots);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AddOpenCodeRootsRecursive(item, roots);
            }
        }
    }

    private static bool LooksLikeOpenCodeLocalPath(string value)
    {
        return value.Length >= 3 &&
               char.IsLetter(value[0]) &&
               value[1] == ':' &&
               (value[2] == Path.DirectorySeparatorChar || value[2] == Path.AltDirectorySeparatorChar);
    }

    private static void AddRootsFromJsonArray(JsonElement root, string propertyName, HashSet<string> roots)
    {
        if (!root.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            AddRoot(item.GetString(), roots);
        }
    }

    private static void AddRootsFromLocalEnvironmentSelections(JsonElement root, HashSet<string> roots)
    {
        if (!root.TryGetProperty("local-env-selections-by-workspace", out var selections) ||
            selections.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in selections.EnumerateObject())
        {
            if (property.Name.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                AddRoot(property.Name["local:".Length..], roots);
            }
        }
    }

    private static void AddRootsFromArrayField(string json, string propertyName, HashSet<string> roots)
    {
        var key = $"\"{propertyName}\"";
        var keyIndex = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
        {
            return;
        }

        var arrayStart = json.IndexOf('[', keyIndex);
        if (arrayStart < 0)
        {
            return;
        }

        var arrayEnd = FindMatchingArrayEnd(json, arrayStart);
        if (arrayEnd <= arrayStart)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json[arrayStart..(arrayEnd + 1)]);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                AddRoot(item.GetString(), roots);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed fallback snippets.
        }
    }

    private static int FindMatchingArrayEnd(string json, int arrayStart)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = arrayStart; i < json.Length; i++)
        {
            var ch = json[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '[')
            {
                depth++;
                continue;
            }

            if (ch != ']')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static void AddRoot(string? rawRoot, HashSet<string> roots)
    {
        if (string.IsNullOrWhiteSpace(rawRoot))
        {
            return;
        }

        var root = rawRoot.Trim().Trim('"');
        if (root.EndsWith($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal))
        {
            root = root[..^2];
        }

        try
        {
            root = Path.GetFullPath(root);
        }
        catch
        {
            return;
        }

        if (Directory.Exists(root))
        {
            roots.Add(root);
        }
    }

    private static bool IsInterestingWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        foreach (var segment in IgnoredPathSegments)
        {
            if (normalized.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) &&
               InterestingExtensions.Contains(extension);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        foreach (var watcher in _workspaceWatchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _workspaceWatchers.Clear();
        _workspaceRoots.Clear();
    }
}
