using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
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
    private const int DefaultPetSpriteFrameWidth = 48;
    private const int DefaultPetSpriteFrameHeight = 48;
    private const int DefaultPetSpriteFrameCount = 6;
    private const int DefaultPetSpriteRowCount = 1;
    private const int DefaultPetSpriteFrameIntervalMs = 180;
    private const int DefaultPetSpriteBackgroundTolerance = 70;

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
    private string _petSpritePath = string.Empty;

    [ObservableProperty]
    private string _petSpriteFrameWidthText = DefaultPetSpriteFrameWidth.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _petSpriteFrameHeightText = DefaultPetSpriteFrameHeight.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _petSpriteFrameCountText = DefaultPetSpriteFrameCount.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _petSpriteRowCountText = DefaultPetSpriteRowCount.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _petSpriteFrameIntervalMsText = DefaultPetSpriteFrameIntervalMs.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _petSpriteIdleRowText = "0";

    [ObservableProperty]
    private string _petSpriteProductiveRowText = "0";

    [ObservableProperty]
    private string _petSpriteDistractedRowText = "0";

    [ObservableProperty]
    private string _petSpritePausedRowText = "0";

    [ObservableProperty]
    private string _petSpriteFocusRowText = "0";

    [ObservableProperty]
    private string _petSpriteBreakpointRowText = "0";

    [ObservableProperty]
    private bool _petSpriteRemoveBackgroundOnImport = true;

    [ObservableProperty]
    private string _petSpriteBackgroundToleranceText = DefaultPetSpriteBackgroundTolerance.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _petSpriteStatusText = string.Empty;

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

    public bool HasPetSpriteStatus => !string.IsNullOrWhiteSpace(PetSpriteStatusText);

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
        SelectedAiProvider = s.AiProvider;
        AiApiBaseUrl = s.AiApiBaseUrl;
        AiApiKey = s.AiApiKey;
        AiModel = s.AiModel;
        AiPersonalityPrompt = s.AiPersonalityPrompt;
        PetSpritePath = s.PetSpritePath;
        PetSpriteFrameWidthText = NormalizePositiveSetting(s.PetSpriteFrameWidth, DefaultPetSpriteFrameWidth);
        PetSpriteFrameHeightText = NormalizePositiveSetting(s.PetSpriteFrameHeight, DefaultPetSpriteFrameHeight);
        PetSpriteFrameCountText = NormalizePositiveSetting(s.PetSpriteFrameCount, DefaultPetSpriteFrameCount);
        PetSpriteRowCountText = NormalizePositiveSetting(s.PetSpriteRowCount, DefaultPetSpriteRowCount);
        PetSpriteFrameIntervalMsText = NormalizePositiveSetting(s.PetSpriteFrameIntervalMs, DefaultPetSpriteFrameIntervalMs);
        PetSpriteIdleRowText = NormalizeZeroBasedSetting(s.PetSpriteIdleRow);
        PetSpriteProductiveRowText = NormalizeZeroBasedSetting(s.PetSpriteProductiveRow);
        PetSpriteDistractedRowText = NormalizeZeroBasedSetting(s.PetSpriteDistractedRow);
        PetSpritePausedRowText = NormalizeZeroBasedSetting(s.PetSpritePausedRow);
        PetSpriteFocusRowText = NormalizeZeroBasedSetting(s.PetSpriteFocusRow);
        PetSpriteBreakpointRowText = NormalizeZeroBasedSetting(s.PetSpriteBreakpointRow);
        PetSpriteRemoveBackgroundOnImport = s.PetSpriteRemoveBackgroundOnImport;
        PetSpriteBackgroundToleranceText = NormalizePositiveSetting(s.PetSpriteBackgroundTolerance, DefaultPetSpriteBackgroundTolerance);
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

    partial void OnPetSpriteStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPetSpriteStatus));
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

    [RelayCommand]
    private void ChoosePetSprite()
    {
        if (!TryReadPetSpriteNumbers(
            out var frameWidth,
            out var frameHeight,
            out var frameCount,
            out var rowCount,
            out _,
            out var validationMessage))
        {
            PetSpriteStatusText = validationMessage;
            return;
        }

        if (!TryReadBackgroundTolerance(out var backgroundTolerance, out validationMessage))
        {
            PetSpriteStatusText = validationMessage;
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "PNG Image (*.png)|*.png",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!string.Equals(Path.GetExtension(dialog.FileName), ".png", StringComparison.OrdinalIgnoreCase))
        {
            PetSpriteStatusText = TranslationService.Instance["Settings_PetSpriteInvalidPng"];
            return;
        }

        if (!TryValidateSpriteDimensions(dialog.FileName, frameWidth, frameHeight, frameCount, rowCount, out validationMessage))
        {
            PetSpriteStatusText = validationMessage;
            return;
        }

        try
        {
            var spritesDir = GetSpritesDirectory();
            Directory.CreateDirectory(spritesDir);

            var fileName = BuildImportedSpriteFileName(dialog.FileName);
            var destinationPath = Path.Combine(spritesDir, fileName);

            if (PetSpriteRemoveBackgroundOnImport)
            {
                RemoveSpriteBackground(dialog.FileName, destinationPath, backgroundTolerance);
            }
            else
            {
                File.Copy(dialog.FileName, destinationPath, overwrite: false);
            }

            PetSpritePath = destinationPath;
            PetSpriteStatusText = PetSpriteRemoveBackgroundOnImport
                ? TranslationService.Instance["Settings_PetSpriteImportedTransparent"]
                : TranslationService.Instance["Settings_PetSpriteImported"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            PetSpriteStatusText = string.Format(
                CultureInfo.CurrentCulture,
                TranslationService.Instance["Settings_PetSpriteImportFailed"],
                ex.Message);
        }
    }

    [RelayCommand]
    private void ResetPetSprite()
    {
        PetSpritePath = string.Empty;
        PetSpriteFrameWidthText = DefaultPetSpriteFrameWidth.ToString(CultureInfo.InvariantCulture);
        PetSpriteFrameHeightText = DefaultPetSpriteFrameHeight.ToString(CultureInfo.InvariantCulture);
        PetSpriteFrameCountText = DefaultPetSpriteFrameCount.ToString(CultureInfo.InvariantCulture);
        PetSpriteRowCountText = DefaultPetSpriteRowCount.ToString(CultureInfo.InvariantCulture);
        PetSpriteFrameIntervalMsText = DefaultPetSpriteFrameIntervalMs.ToString(CultureInfo.InvariantCulture);
        PetSpriteIdleRowText = "0";
        PetSpriteProductiveRowText = "0";
        PetSpriteDistractedRowText = "0";
        PetSpritePausedRowText = "0";
        PetSpriteFocusRowText = "0";
        PetSpriteBreakpointRowText = "0";
        PetSpriteRemoveBackgroundOnImport = true;
        PetSpriteBackgroundToleranceText = DefaultPetSpriteBackgroundTolerance.ToString(CultureInfo.InvariantCulture);
        PetSpriteStatusText = TranslationService.Instance["Settings_PetSpriteResetReady"];
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
        if (!TryValidatePetSpriteSettings(
            out var petSpritePath,
            out var petSpriteFrameWidth,
            out var petSpriteFrameHeight,
            out var petSpriteFrameCount,
            out var petSpriteRowCount,
            out var petSpriteFrameIntervalMs,
            out var petSpriteIdleRow,
            out var petSpriteProductiveRow,
            out var petSpriteDistractedRow,
            out var petSpritePausedRow,
            out var petSpriteFocusRow,
            out var petSpriteBreakpointRow,
            out var petSpriteRemoveBackgroundOnImport,
            out var petSpriteBackgroundTolerance,
            out var petSpriteValidationMessage))
        {
            PetSpriteStatusText = petSpriteValidationMessage;
            throw new InvalidOperationException(petSpriteValidationMessage);
        }

        var s = _settingsService.Settings;
        s.Theme = SelectedTheme;
        s.Language = TranslationService.NormalizeLanguageCode(SelectedLanguage);
        s.LaunchAtStartup = LaunchAtStartup;
        s.CloseBehavior = SelectedCloseBehavior;
        s.AiProvider = SelectedAiProvider;
        s.AiApiBaseUrl = AiApiBaseUrl.Trim();
        s.AiApiKey = AiApiKey.Trim();
        s.AiModel = AiModel.Trim();
        s.AiPersonalityPrompt = AiPersonalityPrompt.Trim();
        s.PetSpritePath = petSpritePath;
        s.PetSpriteFrameWidth = petSpriteFrameWidth;
        s.PetSpriteFrameHeight = petSpriteFrameHeight;
        s.PetSpriteFrameCount = petSpriteFrameCount;
        s.PetSpriteRowCount = petSpriteRowCount;
        s.PetSpriteFrameIntervalMs = petSpriteFrameIntervalMs;
        s.PetSpriteIdleRow = petSpriteIdleRow;
        s.PetSpriteProductiveRow = petSpriteProductiveRow;
        s.PetSpriteDistractedRow = petSpriteDistractedRow;
        s.PetSpritePausedRow = petSpritePausedRow;
        s.PetSpriteFocusRow = petSpriteFocusRow;
        s.PetSpriteBreakpointRow = petSpriteBreakpointRow;
        s.PetSpriteRemoveBackgroundOnImport = petSpriteRemoveBackgroundOnImport;
        s.PetSpriteBackgroundTolerance = petSpriteBackgroundTolerance;
        s.AutoSaveMemories = AutoSaveMemories;
        _settingsService.Save();
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

    private bool TryValidatePetSpriteSettings(
        out string petSpritePath,
        out int frameWidth,
        out int frameHeight,
        out int frameCount,
        out int rowCount,
        out int frameIntervalMs,
        out int idleRow,
        out int productiveRow,
        out int distractedRow,
        out int pausedRow,
        out int focusRow,
        out int breakpointRow,
        out bool removeBackgroundOnImport,
        out int backgroundTolerance,
        out string validationMessage)
    {
        petSpritePath = PetSpritePath.Trim();
        if (!TryReadPetSpriteNumbers(
            out frameWidth,
            out frameHeight,
            out frameCount,
            out rowCount,
            out frameIntervalMs,
            out validationMessage))
        {
            idleRow = 0;
            productiveRow = 0;
            distractedRow = 0;
            pausedRow = 0;
            focusRow = 0;
            breakpointRow = 0;
            removeBackgroundOnImport = PetSpriteRemoveBackgroundOnImport;
            backgroundTolerance = DefaultPetSpriteBackgroundTolerance;
            return false;
        }

        if (!TryReadPetSpritePoseRows(
            rowCount,
            out idleRow,
            out productiveRow,
            out distractedRow,
            out pausedRow,
            out focusRow,
            out breakpointRow,
            out validationMessage))
        {
            removeBackgroundOnImport = PetSpriteRemoveBackgroundOnImport;
            backgroundTolerance = DefaultPetSpriteBackgroundTolerance;
            return false;
        }

        removeBackgroundOnImport = PetSpriteRemoveBackgroundOnImport;
        if (!TryReadBackgroundTolerance(out backgroundTolerance, out validationMessage))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(petSpritePath))
        {
            validationMessage = string.Empty;
            return true;
        }

        if (!string.Equals(Path.GetExtension(petSpritePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = TranslationService.Instance["Settings_PetSpriteInvalidPng"];
            return false;
        }

        if (!File.Exists(petSpritePath))
        {
            validationMessage = TranslationService.Instance["Settings_PetSpriteMissingFile"];
            return false;
        }

        return TryValidateSpriteDimensions(
            petSpritePath,
            frameWidth,
            frameHeight,
            frameCount,
            rowCount,
            out validationMessage);
    }

    private bool TryReadPetSpriteNumbers(
        out int frameWidth,
        out int frameHeight,
        out int frameCount,
        out int rowCount,
        out int frameIntervalMs,
        out string validationMessage)
    {
        var hasFrameWidth = TryReadPositiveInteger(PetSpriteFrameWidthText, out frameWidth);
        var hasFrameHeight = TryReadPositiveInteger(PetSpriteFrameHeightText, out frameHeight);
        var hasFrameCount = TryReadPositiveInteger(PetSpriteFrameCountText, out frameCount);
        var hasRowCount = TryReadPositiveInteger(PetSpriteRowCountText, out rowCount);
        var hasFrameInterval = TryReadPositiveInteger(PetSpriteFrameIntervalMsText, out frameIntervalMs);

        if (!hasFrameWidth || !hasFrameHeight || !hasFrameCount || !hasRowCount || !hasFrameInterval)
        {
            validationMessage = TranslationService.Instance["Settings_PetSpriteInvalidNumbers"];
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private bool TryReadPetSpritePoseRows(
        int rowCount,
        out int idleRow,
        out int productiveRow,
        out int distractedRow,
        out int pausedRow,
        out int focusRow,
        out int breakpointRow,
        out string validationMessage)
    {
        var hasIdleRow = TryReadZeroBasedInteger(PetSpriteIdleRowText, out idleRow);
        var hasProductiveRow = TryReadZeroBasedInteger(PetSpriteProductiveRowText, out productiveRow);
        var hasDistractedRow = TryReadZeroBasedInteger(PetSpriteDistractedRowText, out distractedRow);
        var hasPausedRow = TryReadZeroBasedInteger(PetSpritePausedRowText, out pausedRow);
        var hasFocusRow = TryReadZeroBasedInteger(PetSpriteFocusRowText, out focusRow);
        var hasBreakpointRow = TryReadZeroBasedInteger(PetSpriteBreakpointRowText, out breakpointRow);

        if (!hasIdleRow ||
            !hasProductiveRow ||
            !hasDistractedRow ||
            !hasPausedRow ||
            !hasFocusRow ||
            !hasBreakpointRow)
        {
            validationMessage = TranslationService.Instance["Settings_PetSpriteInvalidRows"];
            return false;
        }

        if (new[] { idleRow, productiveRow, distractedRow, pausedRow, focusRow, breakpointRow }
            .Any(row => row >= rowCount))
        {
            validationMessage = string.Format(
                CultureInfo.CurrentCulture,
                TranslationService.Instance["Settings_PetSpriteRowOutOfRange"],
                rowCount - 1);
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private static bool TryValidateSpriteDimensions(
        string path,
        int frameWidth,
        int frameHeight,
        int frameCount,
        int rowCount,
        out string validationMessage)
    {
        try
        {
            var frame = BitmapFrame.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var requiredWidth = frameWidth * frameCount;
            var requiredHeight = frameHeight * rowCount;
            if (frame.PixelWidth < requiredWidth || frame.PixelHeight < requiredHeight)
            {
                validationMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    TranslationService.Instance["Settings_PetSpriteInvalidSize"],
                    frame.PixelWidth,
                    frame.PixelHeight,
                    requiredWidth,
                    requiredHeight);
                return false;
            }

            validationMessage = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            validationMessage = string.Format(
                CultureInfo.CurrentCulture,
                TranslationService.Instance["Settings_PetSpriteLoadFailed"],
                ex.Message);
            return false;
        }
    }

    private static bool TryReadPositiveInteger(string text, out int value)
    {
        return int.TryParse(
                text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value) &&
            value > 0;
    }

    private static bool TryReadBackgroundTolerance(string text, out int value)
    {
        return int.TryParse(
                text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value) &&
            value is >= 0 and <= 255;
    }

    private bool TryReadBackgroundTolerance(out int value, out string validationMessage)
    {
        if (!TryReadBackgroundTolerance(PetSpriteBackgroundToleranceText, out value))
        {
            validationMessage = TranslationService.Instance["Settings_PetSpriteInvalidTolerance"];
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private static bool TryReadZeroBasedInteger(string text, out int value)
    {
        return int.TryParse(
                text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value) &&
            value >= 0;
    }

    private static string NormalizePositiveSetting(int value, int defaultValue)
    {
        return Math.Max(value, 1) == value
            ? value.ToString(CultureInfo.InvariantCulture)
            : defaultValue.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeZeroBasedSetting(int value)
    {
        return Math.Max(value, 0).ToString(CultureInfo.InvariantCulture);
    }

    private static void RemoveSpriteBackground(string sourcePath, string destinationPath, int tolerance)
    {
        using var source = new Bitmap(sourcePath);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var keyColor = bitmap.GetPixel(0, 0);
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            var byteCount = Math.Abs(data.Stride) * bitmap.Height;
            var pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);

            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = data.Stride > 0
                    ? y * data.Stride
                    : (bitmap.Height - 1 - y) * Math.Abs(data.Stride);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = row + x * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];

                    if (IsWithinTolerance(red, green, blue, keyColor, tolerance))
                    {
                        pixels[offset + 3] = 0;
                    }
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(destinationPath, ImageFormat.Png);
    }

    private static bool IsWithinTolerance(byte red, byte green, byte blue, Color keyColor, int tolerance)
    {
        return Math.Abs(red - keyColor.R) <= tolerance &&
            Math.Abs(green - keyColor.G) <= tolerance &&
            Math.Abs(blue - keyColor.B) <= tolerance;
    }

    private static string GetSpritesDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perelegans",
            "Sprites");
    }

    private static string BuildImportedSpriteFileName(string sourcePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = new string(baseName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "sprite";
        }

        return $"{safeName}_{DateTime.Now:yyyyMMddHHmmssfff}.png";
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
