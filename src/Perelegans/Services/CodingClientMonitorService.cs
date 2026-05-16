using System.Diagnostics;
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
    ClaudeDesktop
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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CodexSignalWindow = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan WorkspaceWriteWindow = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan CompletionVisibleWindow = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan RecentCodingNotificationWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NotificationWaitingVisibleWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WaitingQuietWindow = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan WorkspaceRefreshInterval = TimeSpan.FromSeconds(12);

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
    private readonly Dictionary<string, FileSystemWatcher> _workspaceWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _workspaceRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _codexHome;
    private readonly string _claudeRoamingDir;
    private readonly string _claudeLocalDir;
    private readonly string _claudeLocal3pDir;
    private CodingClientActivitySnapshot _currentSnapshot = CreateIdleSnapshot(DateTime.Now);
    private DateTime _lastCodexSignalAt = DateTime.MinValue;
    private DateTime _lastClaudeSignalAt = DateTime.MinValue;
    private DateTime _lastWorkspaceChangeAt = DateTime.MinValue;
    private DateTime _lastCodingActivityAt = DateTime.MinValue;
    private DateTime _lastWorkspaceRefreshAt = DateTime.MinValue;
    private DateTime _lastNotificationStateAt = DateTime.MinValue;
    private CodingClientKind _lastNotificationClientKind = CodingClientKind.CodexDesktop;
    private CodingClientActivityState _lastNotificationState = CodingClientActivityState.Idle;
    private UserNotificationListener? _notificationListener;
    private string _lastNotificationMessage = string.Empty;
    private string _lastChangedPath = string.Empty;
    private readonly HashSet<uint> _processedNotificationIds = [];
    private bool _disposed;

    public event Action<CodingClientActivitySnapshot>? ActivityChanged;

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

        var codexRunning = IsCodexProcessRunning();
        var hasCodexSignal = UpdateCodexSignalFiles(now, baselineOnly: false);
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

        if (TryGetActiveNotificationState(now, lastChangedPath, out var notificationSnapshot))
        {
            Publish(notificationSnapshot);
            return;
        }

        var hasRecentCodexSignal = now - _lastCodexSignalAt <= CodexSignalWindow;
        var hasRecentWorkspaceChange = now - lastWorkspaceChangeAt <= WorkspaceWriteWindow;
        var hasClientContext = codexRunning || hasRecentCodexSignal;
        if ((hasRecentWorkspaceChange && hasClientContext) || hasRecentCodexSignal)
        {
            _lastCodingActivityAt = now;
            Publish(CreateSnapshot(
                CodingClientActivityState.Coding,
                now,
                "Codex Desktop 正在写代码，我先陪它敲一会儿。",
                lastChangedPath));
            return;
        }

        if (codexRunning &&
            _lastCodingActivityAt != DateTime.MinValue &&
            now - _lastCodingActivityAt >= WaitingQuietWindow)
        {
            Publish(CreateSnapshot(
                CodingClientActivityState.WaitingForConfirmation,
                now,
                "Codex Desktop 可能在等你确认。",
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
             !_settingsService.Settings.ClaudeDesktopMonitorEnabled))
        {
            Publish(CreateIdleSnapshot(now));
            return;
        }

        var codexEnabled = _settingsService.Settings.CodexDesktopMonitorEnabled;
        var claudeEnabled = _settingsService.Settings.ClaudeDesktopMonitorEnabled;
        var codexRunning = codexEnabled && IsCodexProcessRunning();
        var claudeRunning = claudeEnabled && IsClaudeProcessRunning();
        var hasCodexSignal = codexEnabled && UpdateCodexSignalFiles(now, baselineOnly: false);
        var hasClaudeSignal = claudeEnabled && UpdateClaudeSignalFiles(now, baselineOnly: false);
        if (hasCodexSignal || now - _lastWorkspaceRefreshAt >= WorkspaceRefreshInterval)
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

        if (TryGetActiveNotificationState(now, lastChangedPath, out var notificationSnapshot))
        {
            Publish(notificationSnapshot);
            return;
        }

        var hasRecentCodexSignal = now - _lastCodexSignalAt <= CodexSignalWindow;
        var hasRecentClaudeSignal = now - _lastClaudeSignalAt <= CodexSignalWindow;
        var hasRecentWorkspaceChange = now - lastWorkspaceChangeAt <= WorkspaceWriteWindow;
        var hasCodexClientContext = codexRunning || hasRecentCodexSignal;
        if (codexEnabled &&
            ((hasRecentWorkspaceChange && hasCodexClientContext) || hasRecentCodexSignal))
        {
            _lastCodingActivityAt = now;
            Publish(CreateSnapshot(
                CodingClientKind.CodexDesktop,
                CodingClientActivityState.Coding,
                now,
                "Codex Desktop 正在写代码，我先陪它敲一会儿。",
                lastChangedPath));
            return;
        }

        if (claudeEnabled && hasRecentClaudeSignal)
        {
            _lastCodingActivityAt = now;
            Publish(CreateSnapshot(
                CodingClientKind.ClaudeDesktop,
                CodingClientActivityState.Coding,
                now,
                "Claude Desktop 正在生成回复，我先陪它想一会儿。",
                lastChangedPath));
            return;
        }

        if (codexEnabled &&
            codexRunning &&
            _lastCodingActivityAt != DateTime.MinValue &&
            now - _lastCodingActivityAt >= WaitingQuietWindow)
        {
            Publish(CreateSnapshot(
                CodingClientKind.CodexDesktop,
                CodingClientActivityState.WaitingForConfirmation,
                now,
                "Codex Desktop 可能在等你确认。",
                lastChangedPath));
            return;
        }

        if (claudeEnabled &&
            claudeRunning &&
            _lastClaudeSignalAt != DateTime.MinValue &&
            now - _lastClaudeSignalAt >= WaitingQuietWindow &&
            now - _lastClaudeSignalAt <= RecentCodingNotificationWindow)
        {
            Publish(CreateSnapshot(
                CodingClientKind.ClaudeDesktop,
                CodingClientActivityState.WaitingForConfirmation,
                now,
                "Claude Desktop 可能在等你继续。",
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
             !_settingsService.Settings.ClaudeDesktopMonitorEnabled))
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
             !_settingsService.Settings.ClaudeDesktopMonitorEnabled))
        {
            return;
        }

        if (IsApprovalNotification(combinedText))
        {
            MarkClientState(
                clientKind,
                CodingClientActivityState.WaitingForConfirmation,
                $"{GetClientName(clientKind)} 在等你确认。");
            return;
        }

        if (IsCompletionNotification(combinedText))
        {
            MarkClientState(
                clientKind,
                CodingClientActivityState.Completed,
                $"{GetClientName(clientKind)} 刚发来完成通知，去看看吧。");
        }
    }

    private static bool TryGetNotificationClientKind(string appName, out CodingClientKind kind)
    {
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
            _lastCodingActivityAt = now;
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

        var visibleWindow = state == CodingClientActivityState.Completed
            ? CompletionVisibleWindow
            : NotificationWaitingVisibleWindow;
        if (now - updatedAt > visibleWindow)
        {
            return false;
        }

        snapshot = CreateSnapshot(kind, state, now, message, lastChangedPath);
        return true;
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
            .Take(8)
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
                Debug.WriteLine($"Failed to watch Codex workspace '{root}': {ex.Message}");
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
            kind == CodingClientKind.CodexDesktop ? workspace : string.Empty,
            lastChangedPath,
            now);
    }

    private static string GetClientName(CodingClientKind kind)
    {
        return kind == CodingClientKind.ClaudeDesktop
            ? "Claude Desktop"
            : "Codex Desktop";
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
            "approve",
            "confirm",
            "confirmation",
            "permission",
            "allow",
            "review",
            "needs your",
            "waiting for",
            "确认",
            "批准",
            "允许",
            "权限",
            "等待",
            "需要你");
    }

    private static bool IsCompletionNotification(string text)
    {
        return ContainsAny(
            text,
            "complete",
            "completed",
            "done",
            "finished",
            "ready for review",
            "response ready",
            "all set",
            "完成",
            "已完成",
            "写完",
            "写好了",
            "准备好了",
            "生成完",
            "生成完成",
            "回复完成",
            "处理完");
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
