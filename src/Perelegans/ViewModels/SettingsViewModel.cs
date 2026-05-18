using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Perelegans.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private static readonly TimeSpan AiTestTimeout = TimeSpan.FromSeconds(30);

    private readonly ThemeService _themeService;
    private readonly SettingsService _settingsService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly DatabaseService _databaseService;
    private readonly ProcessMonitorService? _processMonitor;

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    [ObservableProperty]
    private string _selectedLanguage = "zh-Hans";

    [ObservableProperty]
    private bool _launchAtStartup;

    [ObservableProperty]
    private AppCloseBehavior _selectedCloseBehavior;

    [ObservableProperty]
    private bool _codingClientMonitorEnabled;

    [ObservableProperty]
    private bool _codexDesktopMonitorEnabled;

    [ObservableProperty]
    private bool _claudeDesktopMonitorEnabled;

    [ObservableProperty]
    private string _productiveProcessRules = string.Empty;

    [ObservableProperty]
    private int _breakpointIdleThresholdMinutes;

    [ObservableProperty]
    private int _breakpointReturnThresholdSeconds;

    [ObservableProperty]
    private AiProvider _selectedAiProvider;

    [ObservableProperty]
    private string _aiApiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    [ObservableProperty]
    private string _aiModel = string.Empty;

    [ObservableProperty]
    private string _aiPersonalityPrompt = string.Empty;

    [ObservableProperty]
    private string _aiTestStatusText = string.Empty;

    [ObservableProperty]
    private bool _isTestingAi;

    [ObservableProperty]
    private string _dataMaintenanceStatusText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ContextMemory> _memories = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMemoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteMemoryCommand))]
    private ContextMemory? _selectedMemory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMemoryCommand))]
    private string _memoryTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMemoryCommand))]
    private string _memoryContent = string.Empty;

    [ObservableProperty]
    private string _memoryTags = string.Empty;

    [ObservableProperty]
    private ContextMemoryType _selectedMemoryType = ContextMemoryType.Note;

    [ObservableProperty]
    private bool _autoSaveMemories;

    [ObservableProperty]
    private string _memoryStatusText = string.Empty;

    public bool HasDataMaintenanceStatus => !string.IsNullOrWhiteSpace(DataMaintenanceStatusText);

    public bool HasAiTestStatus => !string.IsNullOrWhiteSpace(AiTestStatusText);

    public bool HasMemoryStatus => !string.IsNullOrWhiteSpace(MemoryStatusText);

    public string[] LanguageOptions { get; } = ["zh-Hans", "en-US"];

    public IReadOnlyList<ContextMemoryType> MemoryTypeOptions { get; } =
    [
        ContextMemoryType.Preference,
        ContextMemoryType.Project,
        ContextMemoryType.Decision,
        ContextMemoryType.Workflow,
        ContextMemoryType.Application,
        ContextMemoryType.Note,
        ContextMemoryType.Review
    ];

    public IReadOnlyList<AppCloseBehaviorOption> CloseBehaviorOptions { get; } =
    [
        new(AppCloseBehavior.Exit, TranslationService.Instance["Settings_CloseBehaviorExit"]),
        new(AppCloseBehavior.MinimizeToTray, TranslationService.Instance["Settings_CloseBehaviorTray"])
    ];

    public IReadOnlyList<AiProviderOption> AiProviderOptions { get; } =
    [
        new(AiProvider.Auto, TranslationService.Instance["Settings_AiProvider_Auto"]),
        new(AiProvider.OpenAI, TranslationService.Instance["Settings_AiProvider_OpenAI"]),
        new(AiProvider.OpenRouter, TranslationService.Instance["Settings_AiProvider_OpenRouter"]),
        new(AiProvider.Anthropic, TranslationService.Instance["Settings_AiProvider_Anthropic"])
    ];

    public string AiBaseUrlPlaceholder => SelectedAiProvider switch
    {
        AiProvider.Anthropic => "https://api.anthropic.com",
        AiProvider.OpenRouter => "https://openrouter.ai/api/v1",
        _ => "https://api.openai.com/v1"
    };

    public string AiApiKeyPlaceholder => SelectedAiProvider == AiProvider.Anthropic
        ? "sk-ant-..."
        : "sk-...";

    public string AiModelPlaceholder => SelectedAiProvider switch
    {
        AiProvider.Anthropic => "claude-3-5-sonnet-latest",
        AiProvider.OpenRouter => "anthropic/claude-3.5-sonnet",
        _ => "gpt-4.1-mini"
    };

    public string AiProviderHint => SelectedAiProvider switch
    {
        AiProvider.Anthropic => TranslationService.Instance["Settings_AiProviderHint_Anthropic"],
        AiProvider.OpenRouter => TranslationService.Instance["Settings_AiProviderHint_OpenRouter"],
        AiProvider.OpenAI => TranslationService.Instance["Settings_AiProviderHint_OpenAI"],
        _ => TranslationService.Instance["Settings_AiProviderHint_Auto"]
    };

    public SettingsViewModel(
        ThemeService themeService,
        SettingsService settingsService,
        StartupRegistrationService startupRegistrationService,
        DatabaseService databaseService,
        ProcessMonitorService? processMonitor)
    {
        _themeService = themeService;
        _settingsService = settingsService;
        _startupRegistrationService = startupRegistrationService;
        _databaseService = databaseService;
        _processMonitor = processMonitor;

        var s = _settingsService.Settings;
        SelectedTheme = s.Theme;
        SelectedLanguage = TranslationService.NormalizeLanguageCode(s.Language);
        LaunchAtStartup = s.LaunchAtStartup;
        SelectedCloseBehavior = s.CloseBehavior;
        CodingClientMonitorEnabled = s.CodingClientMonitorEnabled;
        CodexDesktopMonitorEnabled = s.CodexDesktopMonitorEnabled;
        ClaudeDesktopMonitorEnabled = s.ClaudeDesktopMonitorEnabled;
        ProductiveProcessRules = s.ProductiveProcessRules;
        BreakpointIdleThresholdMinutes = s.BreakpointIdleThresholdMinutes;
        BreakpointReturnThresholdSeconds = s.BreakpointReturnThresholdSeconds;
        SelectedAiProvider = s.AiProvider;
        AiApiBaseUrl = s.AiApiBaseUrl;
        AiApiKey = s.AiApiKey;
        AiModel = s.AiModel;
        AiPersonalityPrompt = s.AiPersonalityPrompt;
        AutoSaveMemories = s.AutoSaveMemories;
        _ = RefreshMemoriesAsync();
    }

    partial void OnSelectedAiProviderChanged(AiProvider value)
    {
        OnPropertyChanged(nameof(AiBaseUrlPlaceholder));
        OnPropertyChanged(nameof(AiApiKeyPlaceholder));
        OnPropertyChanged(nameof(AiModelPlaceholder));
        OnPropertyChanged(nameof(AiProviderHint));
    }

    partial void OnAiTestStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasAiTestStatus));
    }

    partial void OnDataMaintenanceStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDataMaintenanceStatus));
    }

    partial void OnMemoryStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasMemoryStatus));
    }

    partial void OnSelectedMemoryChanged(ContextMemory? value)
    {
        if (value == null)
        {
            MemoryTitle = string.Empty;
            MemoryContent = string.Empty;
            MemoryTags = string.Empty;
            SelectedMemoryType = ContextMemoryType.Note;
            return;
        }

        MemoryTitle = value.Title;
        MemoryContent = value.Content;
        MemoryTags = value.Tags;
        SelectedMemoryType = value.Type;
    }

    partial void OnIsTestingAiChanged(bool value)
    {
        TestAiCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestAi() => !IsTestingAi;

    [RelayCommand]
    private void ResetAiPersonalityPrompt()
    {
        var result = System.Windows.MessageBox.Show(
            TranslationService.Instance["Settings_AiPersonalityPromptResetConfirm"],
            TranslationService.Instance["Settings_WindowTitle"],
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        AiPersonalityPrompt = AppSettings.DefaultAiPersonalityPrompt;
    }

    [RelayCommand(CanExecute = nameof(CanTestAi))]
    private async Task TestAiAsync()
    {
        if (string.IsNullOrWhiteSpace(AiApiBaseUrl) ||
            string.IsNullOrWhiteSpace(AiApiKey) ||
            string.IsNullOrWhiteSpace(AiModel))
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestMissingConfig"];
            return;
        }

        if (!Uri.TryCreate(AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestInvalidUrl"];
            return;
        }

        IsTestingAi = true;
        AiTestStatusText = TranslationService.Instance["Settings_AiTestRunning"];

        try
        {
            var testSettings = new AppSettings
            {
                AiProvider = SelectedAiProvider,
                AiApiBaseUrl = AiApiBaseUrl.Trim(),
                AiApiKey = AiApiKey.Trim(),
                AiModel = AiModel.Trim()
            };

            using var httpClient = AppHttpClientFactory.Create(testSettings);
            using var timeoutCts = new CancellationTokenSource(AiTestTimeout);
            var content = await SendAiTestMessageAsync(httpClient, baseUri, testSettings, timeoutCts.Token);

            if (TryExtractTestMessage(content, out var message))
            {
                AiTestStatusText = string.Format(
                    TranslationService.Instance["Settings_AiTestSuccess"],
                    $"{AiModel.Trim()} ({message})");
            }
            else
            {
                AiTestStatusText = TranslationService.Instance["Settings_AiTestInvalidResponse"];
            }
        }
        catch (OperationCanceledException)
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestTimeout"];
        }
        catch (HttpRequestException ex)
        {
            AiTestStatusText = string.Format(
                TranslationService.Instance["Settings_AiTestFailed"],
                ex.Message);
        }
        catch (JsonException)
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestInvalidResponse"];
        }
        finally
        {
            IsTestingAi = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Settings;
        s.Theme = SelectedTheme;
        s.Language = TranslationService.NormalizeLanguageCode(SelectedLanguage);
        s.LaunchAtStartup = LaunchAtStartup;
        s.CloseBehavior = SelectedCloseBehavior;
        s.CodingClientMonitorEnabled = CodingClientMonitorEnabled;
        s.CodexDesktopMonitorEnabled = CodexDesktopMonitorEnabled;
        s.ClaudeDesktopMonitorEnabled = ClaudeDesktopMonitorEnabled;
        s.ProductiveProcessRules = ProductiveProcessRules.Trim();
        s.BreakpointIdleThresholdMinutes = Math.Clamp(BreakpointIdleThresholdMinutes, 1, 240);
        s.BreakpointReturnThresholdSeconds = Math.Clamp(BreakpointReturnThresholdSeconds, 1, 120);
        s.AiProvider = SelectedAiProvider;
        s.AiApiBaseUrl = AiApiBaseUrl.Trim();
        s.AiApiKey = AiApiKey.Trim();
        s.AiModel = AiModel.Trim();
        s.AiPersonalityPrompt = AiPersonalityPrompt.Trim();
        s.AutoSaveMemories = AutoSaveMemories;
        _settingsService.Save();
        _processMonitor?.SetProductivityRules(s.ProductiveProcessRules);
        _themeService.ApplyTheme(s.Theme);
        TranslationService.Instance.ChangeLanguage(s.Language);
        _startupRegistrationService.SetEnabled(s.LaunchAtStartup);
    }

    [RelayCommand]
    private async Task SaveBackupAsync()
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

        await _databaseService.BackupDatabaseAsync(dialog.FileName);
        DataMaintenanceStatusText = TranslationService.Instance["Settings_DataBackupSaved"];
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQLite Database (*.db)|*.db"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var shouldResumeMonitor = _processMonitor?.IsRunning == true;
        if (shouldResumeMonitor)
        {
            await _processMonitor!.StopAsync();
        }

        await _databaseService.RestoreDatabaseAsync(dialog.FileName);

        if (shouldResumeMonitor && _settingsService.Settings.MonitorEnabled)
        {
            _processMonitor!.Start();
        }

        DataMaintenanceStatusText = TranslationService.Instance["Settings_DataBackupRestored"];
        await RefreshMemoriesAsync();
    }

    [RelayCommand]
    private async Task RefreshMemoriesAsync()
    {
        Memories = new ObservableCollection<ContextMemory>(await _databaseService.GetContextMemoriesAsync());
    }

    [RelayCommand]
    private void NewMemory()
    {
        SelectedMemory = null;
        MemoryTitle = string.Empty;
        MemoryContent = string.Empty;
        MemoryTags = string.Empty;
        SelectedMemoryType = ContextMemoryType.Note;
        MemoryStatusText = TranslationService.Instance["Settings_MemoryNewReady"];
    }

    private bool CanSaveMemory()
    {
        return !string.IsNullOrWhiteSpace(MemoryContent);
    }

    [RelayCommand(CanExecute = nameof(CanSaveMemory))]
    private async Task SaveMemoryAsync()
    {
        var saved = await _databaseService.UpsertContextMemoryAsync(
            MemoryTitle,
            MemoryContent,
            SelectedMemoryType,
            "settings",
            MemoryTags,
            0.85,
            SelectedMemory?.Id);

        await RefreshMemoriesAsync();
        SelectedMemory = Memories.FirstOrDefault(memory => memory.Id == saved.Id);
        MemoryStatusText = TranslationService.Instance["Settings_MemorySaved"];
    }

    private bool CanDeleteMemory()
    {
        return SelectedMemory != null;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteMemory))]
    private async Task DeleteMemoryAsync()
    {
        if (SelectedMemory == null)
        {
            return;
        }

        await _databaseService.DeleteContextMemoryAsync(SelectedMemory.Id);
        SelectedMemory = null;
        await RefreshMemoriesAsync();
        MemoryStatusText = TranslationService.Instance["Settings_MemoryDeleted"];
    }

    [RelayCommand]
    private async Task ClearMemoriesAsync()
    {
        await _databaseService.ClearContextMemoriesAsync();
        SelectedMemory = null;
        await RefreshMemoriesAsync();
        MemoryStatusText = TranslationService.Instance["Settings_MemoryCleared"];
    }

    private static async Task<string?> SendAiTestMessageAsync(
        HttpClient httpClient,
        Uri baseUri,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(baseUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey.Trim());

        var payload = new
        {
            model = settings.AiModel.Trim(),
            temperature = 0,
            max_tokens = 60,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are an API availability probe. Return compact JSON only."
                },
                new
                {
                    role = "user",
                    content = "Return JSON with one non-empty string field named message. Example: {\"message\":\"ok\"}"
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        return ExtractOpenAiContent(body);
    }

    private static Uri BuildChatCompletionsUri(Uri baseUri)
    {
        if (baseUri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return baseUri;
        }

        var baseText = baseUri.ToString().TrimEnd('/');
        return new Uri($"{baseText}/chat/completions");
    }

    private static string? ExtractOpenAiContent(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = choices.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined ||
            !first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.GetString();
    }

    private static bool TryExtractTestMessage(string? content, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in new[] { "message", "assistantMessage", "content", "text", "description", "reason" })
        {
            if (root.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
            {
                message = property.GetString()!.Trim();
                return true;
            }
        }

        return false;
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }
}

public sealed class AppCloseBehaviorOption(AppCloseBehavior value, string label)
{
    public AppCloseBehavior Value { get; } = value;
    public string Label { get; } = label;
}

public sealed class AiProviderOption(AiProvider value, string label)
{
    public AiProvider Value { get; } = value;
    public string Label { get; } = label;
}
