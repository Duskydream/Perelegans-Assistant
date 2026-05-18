using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Perelegans.Models;

namespace Perelegans.Services;

public sealed class BreakpointSnapshotService : IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly ProcessMonitorService _processMonitor;
    private readonly DispatcherTimer _timer;
    private BreakpointSnapshot? _pendingSnapshot;
    private bool _capturedThisIdle;
    private bool _isSaving;

    public event Action? AwayDetected;
    public event Action<BreakpointSnapshot>? BreakpointReady;

    public BreakpointSnapshotService(
        DatabaseService databaseService,
        SettingsService settingsService,
        ProcessMonitorService processMonitor)
    {
        _databaseService = databaseService;
        _settingsService = settingsService;
        _processMonitor = processMonitor;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _timer.Tick += OnTimerTick;
    }

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        try
        {
            await TickAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Breakpoint snapshot error: {ex.Message}");
        }
    }

    private async Task TickAsync()
    {
        if (!_settingsService.Settings.MonitorEnabled)
        {
            _capturedThisIdle = false;
            _pendingSnapshot = null;
            return;
        }

        var idle = GetIdleDuration();
        var idleThreshold = TimeSpan.FromMinutes(Math.Clamp(_settingsService.Settings.BreakpointIdleThresholdMinutes, 1, 240));
        var returnedThreshold = TimeSpan.FromSeconds(Math.Clamp(_settingsService.Settings.BreakpointReturnThresholdSeconds, 1, 120));

        if (idle >= idleThreshold)
        {
            if (!_capturedThisIdle)
            {
                await CaptureIdleSnapshotAsync(DateTime.Now - idle, idle);
            }

            return;
        }

        if (idle <= returnedThreshold)
        {
            _capturedThisIdle = false;
            if (_pendingSnapshot is { WasShown: false, IsDismissed: false } snapshot)
            {
                _pendingSnapshot = null;
                snapshot.WasShown = true;
                snapshot.ReturnedAt = DateTime.Now;
                await _databaseService.MarkBreakpointSnapshotShownAsync(snapshot.Id, snapshot.ReturnedAt.Value);
                BreakpointReady?.Invoke(snapshot);
            }
        }
    }

    private async Task CaptureIdleSnapshotAsync(DateTime leftAt, TimeSpan idle)
    {
        _capturedThisIdle = true;
        var snapshot = _processMonitor.SampleForegroundWindowFocus();
        if (snapshot == null)
        {
            return;
        }

        var latestPlan = await _databaseService.GetLatestOpenPlanMemoryAsync();
        if (!snapshot.IsKnownProductivityApp && latestPlan == null)
        {
            return;
        }

        _isSaving = true;
        try
        {
            var breakpoint = new BreakpointSnapshot
            {
                LeftAt = leftAt,
                ProcessName = snapshot.ProcessName,
                WindowTitle = snapshot.WindowTitle,
                ExecutablePath = snapshot.ExecutablePath,
                RelatedPlanTitle = latestPlan?.Title ?? string.Empty,
                RelatedMemoryId = latestPlan?.Id,
                Summary = CreateSummary(snapshot, latestPlan?.Title, leftAt),
                Evidence = CreateEvidence(snapshot, latestPlan?.Title, idle),
                NextStep = CreateNextStep(snapshot, latestPlan?.Title)
            };

            _pendingSnapshot = await _databaseService.SaveBreakpointSnapshotAsync(breakpoint);
            AwayDetected?.Invoke();
        }
        finally
        {
            _isSaving = false;
        }
    }

    private static string CreateSummary(ForegroundFocusSnapshot snapshot, string? planTitle, DateTime leftAt)
    {
        var title = string.IsNullOrWhiteSpace(snapshot.WindowTitle)
            ? snapshot.ProcessName
            : snapshot.WindowTitle;
        var plan = string.IsNullOrWhiteSpace(planTitle)
            ? string.Empty
            : $"，关联的未完成计划是「{planTitle}」";
        return $"你在 {leftAt:HH:mm} 离开前停在「{title}」{plan}。";
    }

    private static string CreateEvidence(ForegroundFocusSnapshot snapshot, string? planTitle, TimeSpan idle)
    {
        var title = string.IsNullOrWhiteSpace(snapshot.WindowTitle)
            ? "未读取到窗口标题"
            : snapshot.WindowTitle;
        var planLine = string.IsNullOrWhiteSpace(planTitle)
            ? "当时没有明确匹配到未完成 plan。"
            : $"最近的未完成 plan：{planTitle}。";
        return
            $"前台进程：{snapshot.ProcessName}\n" +
            $"窗口标题：{title}\n" +
            $"离开前本窗口连续停留：{FormatDuration(snapshot.Duration)}\n" +
            $"键鼠空闲：约 {FormatDuration(idle)}\n" +
            planLine;
    }

    private static string CreateNextStep(ForegroundFocusSnapshot snapshot, string? planTitle)
    {
        if (!string.IsNullOrWhiteSpace(planTitle))
        {
            return $"先回到「{planTitle}」：打开刚才的 {snapshot.ProcessName} 窗口，补一句“我刚才卡在什么地方”，再继续最小的一步。";
        }

        return $"先回到 {snapshot.ProcessName}：根据窗口标题恢复刚才的问题，再把下一步写成一句短任务。";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟";
        }

        return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} 分钟";
    }

    private static TimeSpan GetIdleDuration()
    {
        var info = new LastInputInfo
        {
            CbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var elapsed = unchecked((uint)Environment.TickCount - info.DwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTimerTick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);
}
