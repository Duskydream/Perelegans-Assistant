using System;
using System.Collections.ObjectModel;
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
    private readonly Action _openSettings;
    private readonly Action _exitApplication;

    [ObservableProperty]
    private ObservableCollection<ApplicationUsage> _applications = new();

    [ObservableProperty]
    private ObservableCollection<ApplicationUsageSession> _recentSessions = new();

    [ObservableProperty]
    private string _currentProcessName = "Waiting for foreground app";

    [ObservableProperty]
    private string _currentProcessDurationText = "0m";

    [ObservableProperty]
    private string _currentFocusLabel = "Unknown";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public int ApplicationCount => Applications.Count;
    public int ProductiveCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Productive);
    public int DistractingCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Distracting);
    public int UnknownCount => Applications.Count(a => a.Category == ApplicationFocusCategory.Unknown);
    public string TotalDurationText => FormatDuration(Applications.Aggregate(TimeSpan.Zero, (total, item) => total + item.TotalDuration));
    public string RecentSessionCountText => RecentSessions.Count.ToString();
    public bool IsMonitorEnabled => _settingsService.Settings.MonitorEnabled;

    public MainViewModel(
        DatabaseService dbService,
        SettingsService settingsService,
        ProcessMonitorService processMonitor,
        Action openSettings,
        Action exitApplication)
    {
        _dbService = dbService;
        _settingsService = settingsService;
        _processMonitor = processMonitor;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _processMonitor.ForegroundFocusUpdated += OnForegroundFocusUpdated;
        TranslationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                RefreshComputedStats();
            }
        };
    }

    public async Task InitializeAsync()
    {
        await _dbService.EnsureDatabaseCreatedAsync();
        await RefreshAsync();

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
            StatusText = $"Last refreshed {DateTime.Now:HH:mm:ss}";
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
            "Clear all recorded application usage data?",
            "FocusArchive",
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
        StatusText = "Backup saved.";
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

        StatusText = "Backup restored.";
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
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _openSettings();
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
        CurrentFocusLabel = snapshot.IsKnownProductivityApp ? "Likely productive" : "Unclassified";
    }

    private void RefreshComputedStats()
    {
        OnPropertyChanged(nameof(ApplicationCount));
        OnPropertyChanged(nameof(ProductiveCount));
        OnPropertyChanged(nameof(DistractingCount));
        OnPropertyChanged(nameof(UnknownCount));
        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(RecentSessionCountText));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))}m";
        }

        return $"{Math.Max(0, (int)Math.Round(duration.TotalSeconds))}s";
    }
}
