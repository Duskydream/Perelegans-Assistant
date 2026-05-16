using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class FloatingPetWindow : Window
{
    private const int SpriteFrameWidth = 48;
    private const int SpriteFrameHeight = 48;
    private const int SpriteFrameCount = 6;

    private readonly DispatcherTimer _spriteTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };
    private BitmapSource? _spriteSheet;
    private string _currentPetMood = "idle";
    private int _spriteFrameIndex;

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
        Loaded -= OnLoaded;
        DataContextChanged -= OnDataContextChanged;

        if (DataContext is FloatingPetViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetPetMood(DataContext is FloatingPetViewModel vm ? vm.PetMood : "idle");
        _spriteTimer.Start();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FloatingPetViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is FloatingPetViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            SetPetMood(newVm.PetMood);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatingPetViewModel.PetMood) &&
            sender is FloatingPetViewModel vm)
        {
            SetPetMood(vm.PetMood);
        }
    }

    private void OnSpriteTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        _spriteFrameIndex = (_spriteFrameIndex + 1) % SpriteFrameCount;
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
                _spriteFrameIndex * SpriteFrameWidth,
                0,
                SpriteFrameWidth,
                SpriteFrameHeight));
    }

    private void SetPetMood(string? mood)
    {
        var spriteMood = mood switch
        {
            "focus" or "coding" => "focus",
            "sleep" => "sleep",
            _ => "idle"
        };

        if (_spriteSheet != null && _currentPetMood == spriteMood)
        {
            return;
        }

        _currentPetMood = spriteMood;
        _spriteFrameIndex = 0;
        _spriteSheet = LoadPetSpriteSheet(spriteMood);
        UpdateSpriteFrame();
    }

    private static BitmapSource LoadPetSpriteSheet(string mood)
    {
        var fileName = mood switch
        {
            "focus" => "pixel_cat_focus.png",
            "sleep" => "pixel_cat_sleep.png",
            _ => "pixel_cat_idle.png"
        };

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Images/Pet/{fileName}", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
