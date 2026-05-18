using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perelegans.Models;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class FloatingPetWindow : Window
{
    private const int DefaultSpriteFrameWidth = 48;
    private const int DefaultSpriteFrameHeight = 48;
    private const int DefaultSpriteFrameCount = 6;
    private const int DefaultSpriteFrameIntervalMs = 180;
    private const int LegacySpriteFrameCount = 6;
    private static readonly Uri DefaultSpriteUri = new("pack://application:,,,/Images/Pet/pixel_cat_idle.png", UriKind.Absolute);

    private readonly DispatcherTimer _spriteTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(DefaultSpriteFrameIntervalMs)
    };
    private BitmapSource? _spriteSheet;
    private FloatingPetViewModel? _subscribedViewModel;
    private bool _usingCustomSprite;
    private string _currentPetMood = "idle";
    private string _currentPetSkinId = PetSkinPresets.Pink;
    private int _spriteFrameIndex;
    private int _spriteFrameWidth = DefaultSpriteFrameWidth;
    private int _spriteFrameHeight = DefaultSpriteFrameHeight;
    private int _spriteFrameCount = DefaultSpriteFrameCount;
    private int _spriteRowCount = 1;
    private int _spritePoseRowIndex;

    public FloatingPetWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
        _spriteTimer.Tick += OnSpriteTimerTick;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            if (DataContext is FloatingPetViewModel vm &&
                vm.ShowDashboardCommand.CanExecute(null))
            {
                vm.ShowDashboardCommand.Execute(null);
            }

            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _spriteTimer.Stop();
        _spriteTimer.Tick -= OnSpriteTimerTick;
        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        UnsubscribeFromViewModel();

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToCurrentViewModel();
        ReloadSpriteSheet();
        _spriteTimer.Start();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromViewModel();
        SubscribeToCurrentViewModel();
        ReloadSpriteSheet();
    }

    private void OnSpriteTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        _spriteFrameIndex = (_spriteFrameIndex + 1) % _spriteFrameCount;
        UpdateSpriteFrame();
    }

    private void UpdateSpriteFrame()
    {
        if (_spriteSheet == null)
        {
            return;
        }

        PetSpriteImage.Source = new CroppedBitmap(
            _spriteSheet,
            new Int32Rect(
                _spriteFrameIndex * _spriteFrameWidth,
                _spritePoseRowIndex * _spriteFrameHeight,
                _spriteFrameWidth,
                _spriteFrameHeight));
    }

    private void SubscribeToCurrentViewModel()
    {
        if (_subscribedViewModel != null || DataContext is not FloatingPetViewModel vm)
        {
            return;
        }

        _subscribedViewModel = vm;
        vm.SettingsChanged += OnPetSettingsChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel == null)
        {
            return;
        }

        _subscribedViewModel.SettingsChanged -= OnPetSettingsChanged;
        _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = null;
    }

    private void OnPetSettingsChanged()
    {
        ReloadSpriteSheet();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatingPetViewModel.CurrentPetSpritePose) && _usingCustomSprite)
        {
            UpdateSpritePoseRow();
            UpdateSpriteFrame();
            return;
        }

        if ((e.PropertyName == nameof(FloatingPetViewModel.PetMood) ||
             e.PropertyName == nameof(FloatingPetViewModel.SelectedPetSkinId)) &&
            !_usingCustomSprite)
        {
            ReloadSpriteSheet();
        }
    }

    private void ReloadSpriteSheet()
    {
        _spriteTimer.Stop();
        _spriteFrameIndex = 0;

        var settings = _subscribedViewModel?.Settings;
        if (TryLoadCustomSprite(settings))
        {
            _usingCustomSprite = true;
        }
        else
        {
            _usingCustomSprite = false;
            LoadPresetOrLegacySprite();
        }

        UpdateSpritePoseRow();
        UpdateSpriteFrame();

        if (IsLoaded)
        {
            _spriteTimer.Start();
        }
    }

    private bool TryLoadCustomSprite(AppSettings? settings)
    {
        if (settings == null || string.IsNullOrWhiteSpace(settings.PetSpritePath))
        {
            return false;
        }

        var frameWidth = NormalizePositive(settings.PetSpriteFrameWidth, DefaultSpriteFrameWidth);
        var frameHeight = NormalizePositive(settings.PetSpriteFrameHeight, DefaultSpriteFrameHeight);
        var frameCount = NormalizePositive(settings.PetSpriteFrameCount, DefaultSpriteFrameCount);
        var rowCount = NormalizePositive(settings.PetSpriteRowCount, 1);

        if (!Uri.TryCreate(settings.PetSpritePath, UriKind.Absolute, out var customSpriteUri) ||
            !TryLoadPetSpriteSheet(customSpriteUri, out var customSprite) ||
            !IsSpriteSheetLargeEnough(customSprite, frameWidth, frameHeight, frameCount, rowCount))
        {
            return false;
        }

        _spriteFrameWidth = frameWidth;
        _spriteFrameHeight = frameHeight;
        _spriteFrameCount = frameCount;
        _spriteRowCount = rowCount;
        _spriteTimer.Interval = TimeSpan.FromMilliseconds(
            NormalizePositive(settings.PetSpriteFrameIntervalMs, DefaultSpriteFrameIntervalMs));
        _spriteSheet = customSprite;
        return true;
    }

    private void LoadPresetOrLegacySprite()
    {
        _currentPetMood = _subscribedViewModel?.PetMood ?? "idle";
        _currentPetSkinId = _subscribedViewModel?.SelectedPetSkinId ?? PetSkinPresets.Pink;
        _spriteSheet = LoadPetSpriteSheet(_currentPetMood, _currentPetSkinId);
        _spriteFrameCount = LegacySpriteFrameCount;
        _spriteRowCount = 1;
        _spritePoseRowIndex = 0;
        _spriteFrameWidth = Math.Max(1, _spriteSheet.PixelWidth / LegacySpriteFrameCount);
        _spriteFrameHeight = Math.Max(1, _spriteSheet.PixelHeight);
        _spriteTimer.Interval = TimeSpan.FromMilliseconds(DefaultSpriteFrameIntervalMs);
    }

    private static BitmapSource LoadPetSpriteSheet(string mood, string skinId)
    {
        var normalizedSkinId = PetSkinPresets.Normalize(skinId);
        var spriteMood = mood switch
        {
            "focus" or "coding" => "focus",
            "sleep" => "sleep",
            _ => "idle"
        };

        try
        {
            return LoadPackBitmap($"Images/Pet/Skins/{normalizedSkinId}_{spriteMood}.png");
        }
        catch
        {
            return LoadLegacyPetSpriteSheet(spriteMood);
        }
    }

    private static BitmapSource LoadLegacyPetSpriteSheet(string mood)
    {
        var fileName = mood switch
        {
            "focus" => "pixel_cat_focus.png",
            "sleep" => "pixel_cat_sleep.png",
            _ => "pixel_cat_idle.png"
        };

        try
        {
            return LoadPackBitmap($"Images/Pet/{fileName}");
        }
        catch
        {
            return LoadDefaultPetSpriteSheet();
        }
    }

    private static BitmapSource LoadDefaultPetSpriteSheet()
    {
        return TryLoadPetSpriteSheet(DefaultSpriteUri, out var image)
            ? image
            : throw new InvalidOperationException("Default pet sprite could not be loaded.");
    }

    private static BitmapSource LoadPackBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool TryLoadPetSpriteSheet(Uri uri, out BitmapSource image)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.EndInit();
            bitmap.Freeze();

            image = bitmap;
            return true;
        }
        catch
        {
            image = null!;
            return false;
        }
    }

    private void UpdateSpritePoseRow()
    {
        if (!_usingCustomSprite)
        {
            _spritePoseRowIndex = 0;
            return;
        }

        var settings = _subscribedViewModel?.Settings;
        var pose = _subscribedViewModel?.CurrentPetSpritePose ?? PetSpritePose.Idle;
        _spritePoseRowIndex = Math.Clamp(GetConfiguredPoseRow(settings, pose), 0, _spriteRowCount - 1);
    }

    private static int GetConfiguredPoseRow(AppSettings? settings, PetSpritePose pose)
    {
        if (settings == null)
        {
            return 0;
        }

        return pose switch
        {
            PetSpritePose.Productive => settings.PetSpriteProductiveRow,
            PetSpritePose.Distracted => settings.PetSpriteDistractedRow,
            PetSpritePose.Paused => settings.PetSpritePausedRow,
            PetSpritePose.Focus => settings.PetSpriteFocusRow,
            PetSpritePose.Breakpoint => settings.PetSpriteBreakpointRow,
            _ => settings.PetSpriteIdleRow
        };
    }

    private static bool IsSpriteSheetLargeEnough(BitmapSource image, int frameWidth, int frameHeight, int frameCount, int rowCount)
    {
        return image.PixelWidth >= frameWidth * frameCount &&
            image.PixelHeight >= frameHeight * rowCount;
    }

    private static int NormalizePositive(int? value, int defaultValue)
    {
        return value.GetValueOrDefault() > 0
            ? value.GetValueOrDefault()
            : defaultValue;
    }
}
