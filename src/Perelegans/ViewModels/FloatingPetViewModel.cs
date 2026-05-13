using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class FloatingPetViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan AiClassificationCooldown = TimeSpan.FromSeconds(45);
    private readonly ProcessMonitorService _processMonitor;
    private readonly FocusClassificationClient _focusClassificationClient;
    private readonly DatabaseService _databaseService;
    private readonly Action _showDashboard;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly DispatcherTimer _timer;
    private DateTime _lastAiClassificationAt = DateTime.MinValue;
    private string _lastClassifiedProcess = string.Empty;
    private bool _isClassifying;

    [ObservableProperty]
    private string _bubbleText = "专注模式已待命";

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
        Action showDashboard,
        Action openSettings,
        Action exitApplication)
    {
        _processMonitor = processMonitor;
        _focusClassificationClient = focusClassificationClient;
        _databaseService = databaseService;
        _showDashboard = showDashboard;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        SampleNow();
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
    private void Exit()
    {
        _exitApplication();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await SampleNowAsync();
    }

    private void SampleNow()
    {
        var snapshot = _processMonitor.SampleForegroundWindowFocus();
        ApplySnapshot(snapshot);
    }

    private async Task SampleNowAsync()
    {
        var snapshot = _processMonitor.SampleForegroundWindowFocus();
        ApplySnapshot(snapshot);

        if (snapshot == null ||
            _isClassifying ||
            snapshot.Duration < TimeSpan.FromSeconds(20) ||
            DateTime.Now - _lastAiClassificationAt < AiClassificationCooldown ||
            string.Equals(_lastClassifiedProcess, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isClassifying = true;
        try
        {
            var result = await _focusClassificationClient.ClassifyAsync(snapshot.ProcessName, snapshot.Duration);
            _lastAiClassificationAt = DateTime.Now;
            _lastClassifiedProcess = snapshot.ProcessName;

            if (result == null)
            {
                return;
            }

            IsProductive = result.IsProductive;
            BubbleText = string.IsNullOrWhiteSpace(result.Message)
                ? result.Description
                : result.Message;

            await _databaseService.UpdateApplicationAssessmentAsync(
                snapshot.ProcessName,
                result.IsProductive,
                result.Description,
                BubbleText);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
        }
        finally
        {
            _isClassifying = false;
        }
    }

    private void ApplySnapshot(ForegroundFocusSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            BubbleText = "正在等待前台应用信号";
            return;
        }

        CurrentProcessName = snapshot.ProcessName;
        CurrentDuration = snapshot.Duration;
        IsProductive = snapshot.IsKnownProductivityApp;

        if (!_focusClassificationClient.IsConfigured)
        {
            var minutes = Math.Max(1, (int)Math.Round(snapshot.Duration.TotalMinutes));
            BubbleText = snapshot.IsKnownProductivityApp
                ? $"{snapshot.ProcessName} 已专注 {minutes} 分钟"
                : $"{snapshot.ProcessName} 已停留 {minutes} 分钟";
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
