using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Converters;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class MetadataViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private readonly CoverArtService _coverArtService;
    private readonly bool _isNewGame;
    private readonly string _coverCacheKey;
    private bool _suppressCoverFieldSync;
    private double? _editCoverAspectRatio;

    public Game TargetGame { get; }

    public string[] SourceOptions { get; } = [];

    public IReadOnlyList<GameStatusOption> StatusOptions { get; } =
    [
        new(GameStatus.Planned, TranslationService.Instance["GameStatus_Planned"]),
        new(GameStatus.Playing, TranslationService.Instance["GameStatus_Playing"]),
        new(GameStatus.Completed, TranslationService.Instance["GameStatus_Completed"]),
        new(GameStatus.Dropped, TranslationService.Instance["GameStatus_Dropped"])
    ];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedSource = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private ObservableCollection<MetadataResult> _searchResults = new();

    [ObservableProperty]
    private MetadataResult? _selectedResult;

    [ObservableProperty]
    private bool _isSearchEnabled;

    [ObservableProperty]
    private string _editTitle = string.Empty;

    [ObservableProperty]
    private string _editBrand = string.Empty;

    [ObservableProperty]
    private DateTime? _editReleaseDate;

    [ObservableProperty]
    private GameStatus _editStatus;

    [ObservableProperty]
    private string _editWebsite = string.Empty;

    [ObservableProperty]
    private string _editCoverImagePath = string.Empty;

    [ObservableProperty]
    private string _editCoverImageUrl = string.Empty;

    [ObservableProperty]
    private string _coverPreviewSource = string.Empty;

    [ObservableProperty]
    private string _coverStatusText = string.Empty;

    [ObservableProperty]
    private string _metadataStatusText = string.Empty;

    [ObservableProperty]
    private string _editTagsText = string.Empty;

    [ObservableProperty]
    private string _editProcessName = string.Empty;

    [ObservableProperty]
    private string _editExecutablePath = string.Empty;

    public MetadataViewModel(
        Game game,
        HttpClient httpClient,
        DatabaseService dbService,
        SettingsService? settingsService = null,
        bool isNewGame = false,
        bool isSearchEnabled = false)
    {
        TargetGame = game;
        _dbService = dbService;
        _coverArtService = new CoverArtService(httpClient);
        _isNewGame = isNewGame;
        _coverCacheKey = game.Id > 0 ? $"entry-{game.Id}" : $"draft-{Guid.NewGuid():N}";

        IsSearchEnabled = false;
        SearchQuery = game.Title;
        EditTitle = game.Title;
        EditBrand = game.Brand;
        EditReleaseDate = game.ReleaseDate;
        EditStatus = game.Status;
        EditWebsite = game.OfficialWebsite ?? string.Empty;
        EditCoverImagePath = game.CoverImagePath ?? string.Empty;
        EditCoverImageUrl = game.CoverImageUrl ?? string.Empty;
        _editCoverAspectRatio = game.CoverAspectRatio;
        EditTagsText = TagUtilities.ToMultilineText(TagUtilities.Deserialize(game.Tags));
        EditProcessName = game.ProcessName ?? string.Empty;
        EditExecutablePath = game.ExecutablePath ?? string.Empty;

        RefreshCoverPreview();
    }

    partial void OnEditCoverImagePathChanged(string value)
    {
        if (_suppressCoverFieldSync)
            return;

        var trimmed = value?.Trim() ?? string.Empty;
        if (!string.Equals(trimmed, value, StringComparison.Ordinal))
        {
            _suppressCoverFieldSync = true;
            EditCoverImagePath = trimmed;
            _suppressCoverFieldSync = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(trimmed) && !string.IsNullOrWhiteSpace(EditCoverImageUrl))
        {
            _suppressCoverFieldSync = true;
            EditCoverImageUrl = string.Empty;
            _suppressCoverFieldSync = false;
        }

        _editCoverAspectRatio = CoverArtService.TryReadCoverAspectRatio(trimmed);
        CoverArtImageSourceConverter.InvalidateCache(trimmed);
        CoverStatusText = string.Empty;
        RefreshCoverPreview();
    }

    partial void OnEditCoverImageUrlChanged(string value)
    {
        if (_suppressCoverFieldSync)
            return;

        var trimmed = value?.Trim() ?? string.Empty;
        if (!string.Equals(trimmed, value, StringComparison.Ordinal))
        {
            _suppressCoverFieldSync = true;
            EditCoverImageUrl = trimmed;
            _suppressCoverFieldSync = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(trimmed) && !string.IsNullOrWhiteSpace(EditCoverImagePath))
        {
            _suppressCoverFieldSync = true;
            EditCoverImagePath = string.Empty;
            _suppressCoverFieldSync = false;
        }

        _editCoverAspectRatio = null;
        CoverArtImageSourceConverter.InvalidateCache(trimmed);
        CoverStatusText = string.Empty;
        RefreshCoverPreview();
    }

    [RelayCommand]
    private Task Search()
    {
        SearchResults.Clear();
        SelectedResult = null;
        IsSearching = false;
        MetadataStatusText = string.Empty;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ApplySelected()
    {
    }

    [RelayCommand]
    private void ImportFromLocal()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All Files (*.*)|*.*",
            Title = TranslationService.Instance["Meta_CoverBrowseTitle"]
        };

        if (dialog.ShowDialog() != true)
            return;

        var importedCover = _coverArtService.ImportLocalCoverToCache(dialog.FileName, _coverCacheKey);
        if (string.IsNullOrWhiteSpace(importedCover?.CachedPath) || !importedCover.AspectRatio.HasValue)
        {
            CoverStatusText = TranslationService.Instance["Meta_CoverInvalidFile"];
            return;
        }

        SetCoverFields(
            path: importedCover.CachedPath,
            url: null,
            aspectRatio: importedCover.AspectRatio,
            statusText: TranslationService.Instance["Meta_CoverSelectedLocal"]);
    }

    [RelayCommand]
    private void ClearCover()
    {
        SetCoverFields(
            path: null,
            url: null,
            aspectRatio: null,
            statusText: TranslationService.Instance["Meta_CoverCleared"]);
    }

    public Task<IReadOnlyList<CoverCandidate>> LoadCoverCandidatesAsync()
    {
        CoverStatusText = TranslationService.Instance["Meta_CoverFetchFailed"];
        return Task.FromResult<IReadOnlyList<CoverCandidate>>(Array.Empty<CoverCandidate>());
    }

    public Task ApplyCoverCandidateAsync(CoverCandidate candidate)
    {
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
            Title = "Select Executable"
        };

        if (dialog.ShowDialog() != true)
            return;

        EditExecutablePath = dialog.FileName;
        if (string.IsNullOrWhiteSpace(EditProcessName))
        {
            EditProcessName = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        var coverPath = EditCoverImagePath.Trim();
        var coverUrl = EditCoverImageUrl.Trim();
        var previousCoverDisplaySource = TargetGame.CoverDisplaySource;

        if (!string.IsNullOrWhiteSpace(coverPath))
        {
            if (!File.Exists(coverPath))
                throw new InvalidOperationException(TranslationService.Instance["Meta_CoverInvalidFile"]);

            var aspectRatio = CoverArtService.TryReadCoverAspectRatio(coverPath);
            if (!aspectRatio.HasValue)
                throw new InvalidOperationException(TranslationService.Instance["Meta_CoverInvalidFile"]);

            _editCoverAspectRatio = aspectRatio;
        }
        else if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) || !IsSupportedCoverUriScheme(uri))
            {
                throw new InvalidOperationException(TranslationService.Instance["Meta_CoverInvalidUrl"]);
            }

            _editCoverAspectRatio = null;
        }
        else
        {
            _editCoverAspectRatio = null;
        }

        var normalizedCoverPath = NullIfWhiteSpace(coverPath);
        var normalizedCoverUrl = NullIfWhiteSpace(coverUrl);

        CoverArtImageSourceConverter.InvalidateCache(previousCoverDisplaySource);
        CoverArtImageSourceConverter.InvalidateCache(normalizedCoverPath);
        CoverArtImageSourceConverter.InvalidateCache(normalizedCoverUrl);

        TargetGame.Title = EditTitle;
        TargetGame.Brand = EditBrand;
        TargetGame.ReleaseDate = EditReleaseDate;
        TargetGame.Status = EditStatus;
        TargetGame.VndbId = null;
        TargetGame.BangumiId = null;
        TargetGame.BangumiRating = null;
        TargetGame.BangumiComment = null;
        TargetGame.BangumiCollectionType = null;
        TargetGame.ErogameSpaceId = null;
        TargetGame.OfficialWebsite = NullIfWhiteSpace(EditWebsite);
        TargetGame.Tags = TagUtilities.Serialize(TagUtilities.ParseMultilineText(EditTagsText));
        TargetGame.ProcessName = EditProcessName;
        TargetGame.ExecutablePath = EditExecutablePath;
        TargetGame.CoverImagePath = normalizedCoverPath;
        TargetGame.CoverImageUrl = normalizedCoverUrl;
        TargetGame.CoverAspectRatio = _editCoverAspectRatio;
        TargetGame.RefreshCoverBindings();

        if (!_isNewGame)
        {
            await _dbService.UpdateGameAsync(TargetGame);
        }
    }

    private void SetCoverFields(string? path, string? url, double? aspectRatio, string statusText)
    {
        var trimmedPath = path?.Trim() ?? string.Empty;
        var trimmedUrl = url?.Trim() ?? string.Empty;

        CoverArtImageSourceConverter.InvalidateCache(CoverPreviewSource);
        CoverArtImageSourceConverter.InvalidateCache(trimmedPath);
        CoverArtImageSourceConverter.InvalidateCache(trimmedUrl);

        _suppressCoverFieldSync = true;
        EditCoverImagePath = trimmedPath;
        EditCoverImageUrl = trimmedUrl;
        _suppressCoverFieldSync = false;

        _editCoverAspectRatio = aspectRatio;
        RefreshCoverPreview(forceNotify: true);
        CoverStatusText = statusText;
    }

    private void RefreshCoverPreview(bool forceNotify = false)
    {
        var coverPath = EditCoverImagePath.Trim();
        var coverUrl = EditCoverImageUrl.Trim();

        var nextPreviewSource = !string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath)
            ? coverPath
            : coverUrl;

        if (string.Equals(CoverPreviewSource, nextPreviewSource, StringComparison.Ordinal))
        {
            if (forceNotify)
            {
                OnPropertyChanged(nameof(CoverPreviewSource));
            }

            return;
        }

        CoverPreviewSource = nextPreviewSource;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static bool IsSupportedCoverUriScheme(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp ||
               uri.Scheme == Uri.UriSchemeHttps ||
               uri.Scheme == Uri.UriSchemeFile;
    }
}

public sealed class GameStatusOption(GameStatus value, string label)
{
    public GameStatus Value { get; } = value;
    public string Label { get; } = label;
}
