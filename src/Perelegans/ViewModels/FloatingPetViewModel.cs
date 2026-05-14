using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class FloatingPetViewModel : ObservableObject, IDisposable
{
    private readonly ProcessMonitorService _processMonitor;
    private readonly FocusClassificationClient _focusClassificationClient;
    private readonly DatabaseService _databaseService;
    private readonly Action _showDashboard;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _bubbleText = "上下文采集已待命";

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

    private void OnTimerTick(object? sender, EventArgs e)
    {
        SampleNow();
    }

    private void SampleNow()
    {
        var snapshot = _processMonitor.SampleForegroundWindowFocus();
        ApplySnapshot(snapshot);
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

        var minutes = Math.Max(1, (int)Math.Round(snapshot.Duration.TotalMinutes));
        BubbleText = snapshot.IsKnownProductivityApp
            ? $"{snapshot.ProcessName} 已采集 {minutes} 分钟"
            : $"{snapshot.ProcessName} 停留 {minutes} 分钟";
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
