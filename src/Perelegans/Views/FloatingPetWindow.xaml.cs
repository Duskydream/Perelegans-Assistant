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
    private int _spriteFrameIndex;

    public FloatingPetWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
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

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _spriteSheet ??= LoadPetSpriteSheet();
        UpdateSpriteFrame();
        _spriteTimer.Start();
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

    private static BitmapSource LoadPetSpriteSheet()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Images/Pet/pixel_cat_idle.png", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
