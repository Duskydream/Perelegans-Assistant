using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly SettingsService _settingsService;
    private readonly StartupRegistrationService _startupRegistrationService;

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    [ObservableProperty]
    private int _monitorIntervalSeconds;

    [ObservableProperty]
    private string _proxyAddress = string.Empty;

    [ObservableProperty]
    private bool _monitorEnabled;

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
    private string _aiTestStatusText = string.Empty;

    [ObservableProperty]
    private bool _isTestingAi;

    public bool HasAiTestStatus => !string.IsNullOrWhiteSpace(AiTestStatusText);

    public string[] LanguageOptions { get; } = ["zh-Hans", "en-US", "ja-JP"];

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
        StartupRegistrationService startupRegistrationService)
    {
        _themeService = themeService;
        _settingsService = settingsService;
        _startupRegistrationService = startupRegistrationService;

        var s = _settingsService.Settings;
        SelectedTheme = s.Theme;
        MonitorIntervalSeconds = Math.Clamp(s.MonitorIntervalSeconds, 1, 60);
        ProxyAddress = s.ProxyAddress;
        MonitorEnabled = s.MonitorEnabled;
        SelectedLanguage = TranslationService.NormalizeLanguageCode(s.Language);
        LaunchAtStartup = s.LaunchAtStartup;
        SelectedCloseBehavior = s.CloseBehavior;
        SelectedAiProvider = s.AiProvider;
        AiApiBaseUrl = s.AiApiBaseUrl;
        AiApiKey = s.AiApiKey;
        AiModel = s.AiModel;
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

    [RelayCommand]
    private void TestAi()
    {
        if (string.IsNullOrWhiteSpace(AiApiBaseUrl) ||
            string.IsNullOrWhiteSpace(AiApiKey) ||
            string.IsNullOrWhiteSpace(AiModel))
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestMissingConfig"];
            return;
        }

        if (!Uri.TryCreate(AiApiBaseUrl.Trim(), UriKind.Absolute, out _))
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestInvalidUrl"];
            return;
        }

        AiTestStatusText = string.Format(
            TranslationService.Instance["Settings_AiTestSuccess"],
            AiModel.Trim());
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Settings;
        s.Theme = SelectedTheme;
        s.MonitorIntervalSeconds = Math.Clamp(MonitorIntervalSeconds, 1, 60);
        s.ProxyAddress = ProxyAddress.Trim();
        s.MonitorEnabled = MonitorEnabled;
        s.Language = TranslationService.NormalizeLanguageCode(SelectedLanguage);
        s.LaunchAtStartup = LaunchAtStartup;
        s.CloseBehavior = SelectedCloseBehavior;
        s.AiProvider = SelectedAiProvider;
        s.AiApiBaseUrl = AiApiBaseUrl.Trim();
        s.AiApiKey = AiApiKey.Trim();
        s.AiModel = AiModel.Trim();

        _settingsService.Save();
        _themeService.ApplyTheme(s.Theme);
        TranslationService.Instance.ChangeLanguage(s.Language);
        _startupRegistrationService.SetEnabled(s.LaunchAtStartup);
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
