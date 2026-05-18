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
    private static readonly Uri DefaultSpriteUri = new("pack://application:,,,/Images/Pet/pixel_cat_idle.png", UriKind.Absolute);

    private readonly DispatcherTimer _spriteTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(DefaultSpriteFrameIntervalMs)
    };
    private BitmapSource? _spriteSheet;
    private FloatingPetViewModel? _subscribedViewModel;
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
        UnsubscribeFromSettingsChanged();

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
        UnsubscribeFromSettingsChanged();
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

    private void UnsubscribeFromSettingsChanged()
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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FloatingPetViewModel.CurrentPetSpritePose))
        {
            return;
        }

        UpdateSpritePoseRow();
        UpdateSpriteFrame();
    }

    private void ReloadSpriteSheet()
    {
        _spriteTimer.Stop();
        _spriteFrameIndex = 0;

        var settings = _subscribedViewModel?.Settings;
        _spriteFrameWidth = NormalizePositive(settings?.PetSpriteFrameWidth, DefaultSpriteFrameWidth);
        _spriteFrameHeight = NormalizePositive(settings?.PetSpriteFrameHeight, DefaultSpriteFrameHeight);
        _spriteFrameCount = NormalizePositive(settings?.PetSpriteFrameCount, DefaultSpriteFrameCount);
        _spriteRowCount = NormalizePositive(settings?.PetSpriteRowCount, 1);
        _spriteTimer.Interval = TimeSpan.FromMilliseconds(
            NormalizePositive(settings?.PetSpriteFrameIntervalMs, DefaultSpriteFrameIntervalMs));

        if (settings != null &&
            !string.IsNullOrWhiteSpace(settings.PetSpritePath) &&
            Uri.TryCreate(settings.PetSpritePath, UriKind.Absolute, out var customSpriteUri) &&
            TryLoadPetSpriteSheet(customSpriteUri, out var customSprite) &&
            IsSpriteSheetLargeEnough(customSprite, _spriteFrameWidth, _spriteFrameHeight, _spriteFrameCount, _spriteRowCount))
        {
            _spriteSheet = customSprite;
        }
        else
        {
            _spriteFrameWidth = DefaultSpriteFrameWidth;
            _spriteFrameHeight = DefaultSpriteFrameHeight;
            _spriteFrameCount = DefaultSpriteFrameCount;
            _spriteRowCount = 1;
            _spriteSheet = LoadDefaultPetSpriteSheet();
        }

        UpdateSpritePoseRow();
        UpdateSpriteFrame();

        if (IsLoaded)
        {
            _spriteTimer.Start();
        }
    }

    private static BitmapSource LoadDefaultPetSpriteSheet()
    {
        return TryLoadPetSpriteSheet(DefaultSpriteUri, out var image)
            ? image
            : throw new InvalidOperationException("Default pet sprite could not be loaded.");
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
