using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Perelegans.Models;

namespace Perelegans.Services;

public class ProcessMonitorService
{
    private readonly DatabaseService _dbService;
    private readonly DispatcherTimer _timer;
    private HashSet<string> _productivityRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code",
        "devenv",
        "WINWORD",
        "EXCEL",
        "POWERPNT",
        "ONENOTE",
        "msedge",
        "chrome",
        "firefox",
        "notion",
        "Obsidian",
        "Teams",
        "OUTLOOK"
    };
    private ForegroundFocusSession? _foregroundFocusSession;

    public bool IsRunning { get; private set; }

    public event Action<ForegroundFocusSnapshot>? ForegroundFocusUpdated;

    public ProcessMonitorService(DatabaseService dbService)
    {
        _dbService = dbService;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;
    }

    public void SetInterval(int seconds)
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    public void SetProductivityRules(string rules)
    {
        var parsed = rules
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(rule => rule.Trim())
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (parsed.Count > 0)
        {
            _productivityRules = parsed;
        }
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _timer.Start();
        SampleForegroundWindowFocus();
    }

    public void Stop()
    {
        _ = StopAsync();
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _timer.Stop();
        IsRunning = false;

        if (_foregroundFocusSession != null)
        {
            await FinalizeForegroundSessionAsync(_foregroundFocusSession, DateTime.Now);
            _foregroundFocusSession = null;
        }
    }

    public ForegroundFocusSnapshot? SampleForegroundWindowFocus()
    {
        var process = TryGetForegroundProcess();
        if (process == null)
        {
            return null;
        }

        try
        {
            var now = DateTime.Now;
            var processName = NormalizeProcessName(TryGetProcessName(process));
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            var executablePath = NormalizeExecutablePath(TryGetExecutablePath(process));
            var windowTitle = NormalizeWindowTitle(TryGetForegroundWindowTitle());
            var isProductivityApp = IsKnownProductivityApp(processName, executablePath, windowTitle);

            if (_foregroundFocusSession == null ||
                _foregroundFocusSession.ProcessId != process.Id ||
                !string.Equals(_foregroundFocusSession.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                if (_foregroundFocusSession != null)
                {
                    QueueFinalizeForegroundSession(_foregroundFocusSession, now);
                }

                _foregroundFocusSession = new ForegroundFocusSession
                {
                    ProcessId = process.Id,
                    ProcessName = processName,
                    ExecutablePath = executablePath,
                    WindowTitle = windowTitle,
                    StartedAt = now,
                    LastSeenAt = now,
                    IsProductivityApp = isProductivityApp
                };
            }
            else
            {
                _foregroundFocusSession.LastSeenAt = now;
                _foregroundFocusSession.ExecutablePath = executablePath;
                _foregroundFocusSession.WindowTitle = windowTitle;
                _foregroundFocusSession.IsProductivityApp = isProductivityApp;
            }

            var snapshot = new ForegroundFocusSnapshot(
                _foregroundFocusSession.ProcessId,
                _foregroundFocusSession.ProcessName,
                _foregroundFocusSession.ExecutablePath,
                _foregroundFocusSession.WindowTitle,
                _foregroundFocusSession.StartedAt,
                now - _foregroundFocusSession.StartedAt,
                _foregroundFocusSession.IsProductivityApp);

            ForegroundFocusUpdated?.Invoke(snapshot);
            return snapshot;
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            SampleForegroundWindowFocus();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProcessMonitor error: {ex.Message}");
        }
    }

    private void QueueFinalizeForegroundSession(ForegroundFocusSession session, DateTime endTime)
    {
        var snapshot = new ForegroundFocusSession
        {
            ProcessId = session.ProcessId,
            ProcessName = session.ProcessName,
            ExecutablePath = session.ExecutablePath,
            StartedAt = session.StartedAt,
            LastSeenAt = endTime,
            IsProductivityApp = session.IsProductivityApp
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await FinalizeForegroundSessionAsync(snapshot, endTime);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save application usage session: {ex.Message}");
            }
        });
    }

    private async Task FinalizeForegroundSessionAsync(ForegroundFocusSession session, DateTime endTime)
    {
        if (endTime <= session.StartedAt)
        {
            return;
        }

        await _dbService.RecordApplicationUsageSessionAsync(
            session.ProcessName,
            session.ExecutablePath,
            session.StartedAt,
            endTime,
            session.IsProductivityApp);
    }

    private sealed class ForegroundFocusSession
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public bool IsProductivityApp { get; set; }
    }

    private static Process? TryGetForegroundProcess()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            return Process.GetProcessById((int)processId);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string? processName)
    {
        return string.IsNullOrWhiteSpace(processName)
            ? string.Empty
            : processName.Trim();
    }

    private static string NormalizeExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        var trimmed = executablePath.Trim().Trim('"');

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string NormalizeWindowTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : title.Trim();
    }

    private bool IsKnownProductivityApp(string processName, string executablePath, string windowTitle)
    {
        return _productivityRules.Any(rule =>
            string.Equals(processName, rule, StringComparison.OrdinalIgnoreCase) ||
            processName.Contains(rule, StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains(rule, StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains(rule, StringComparison.OrdinalIgnoreCase));
    }

    private static string TryGetForegroundWindowTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        return GetWindowText(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed record ForegroundFocusSnapshot(
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    string WindowTitle,
    DateTime StartedAt,
    TimeSpan Duration,
    bool IsKnownProductivityApp);
