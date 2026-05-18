using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.Controls;
using Perelegans.Models;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class MainWindow : MetroWindow
{
    private MainViewModel? _viewModel;
    private ContextMemory? _draggedGalaxyTask;
    private ContentPresenter? _draggedGalaxyPresenter;
    private UIElement? _dragCaptureElement;
    private System.Windows.Point _dragStartPoint;
    private System.Windows.Point _dragStartOffset;
    private bool _isDraggingGalaxyTask;
    private System.Windows.Point _galaxyMapPanStartPoint;
    private System.Windows.Point _galaxyMapPanStartOffset;
    private bool _isPanningGalaxyMap;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        DataContextChanged += MainWindow_DataContextChanged;
        Unloaded += MainWindow_Unloaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as MainViewModel);
        ScrollConversationToEnd();
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as MainViewModel);
    }

    private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(null);
    }

    private void AttachViewModel(MainViewModel? viewModel)
    {
        if (_viewModel == viewModel)
        {
            return;
        }

        if (_viewModel != null)
        {
            _viewModel.ConversationMessages.CollectionChanged -= ConversationMessages_CollectionChanged;
            foreach (var message in _viewModel.ConversationMessages)
            {
                message.PropertyChanged -= ConversationMessage_PropertyChanged;
            }
        }

        _viewModel = viewModel;

        if (_viewModel != null)
        {
            _viewModel.ConversationMessages.CollectionChanged += ConversationMessages_CollectionChanged;
            foreach (var message in _viewModel.ConversationMessages)
            {
                message.PropertyChanged += ConversationMessage_PropertyChanged;
            }
        }
    }

    private void ConversationMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ConversationMessage message in e.NewItems)
            {
                message.PropertyChanged += ConversationMessage_PropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (ConversationMessage message in e.OldItems)
            {
                message.PropertyChanged -= ConversationMessage_PropertyChanged;
            }
        }

        ScrollConversationToEnd();
    }

    private void ConversationMessage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationMessage.Text))
        {
            ScrollConversationToEnd();
        }
    }

    private void ScrollConversationToEnd()
    {
        Dispatcher.BeginInvoke(() => ConversationScrollViewer.ScrollToEnd());
    }

    private void MainSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        if (_viewModel?.ToggleGalaxyCommand.CanExecute(null) == true)
        {
            _viewModel.ToggleGalaxyCommand.Execute(null);
        }
    }

    private void GalaxyScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var delta = e.Delta > 0 ? 0.08 : -0.08;
        var nextScale = Math.Clamp(GalaxyScaleTransform.ScaleX + delta, 0.55, 2.4);
        GalaxyScaleTransform.ScaleX = nextScale;
        GalaxyScaleTransform.ScaleY = nextScale;
        e.Handled = true;
    }

    private void UsagePieChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _viewModel?.ClearUsagePieHover();
    }

    private void UsageSlicePath_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UsageStatsSliceViewModel slice })
        {
            _viewModel?.SetHoveredUsageSlice(slice);
        }
    }

    private void GalaxyMapScrollViewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var nodeContext = FindAncestor<ContentPresenter>(e.OriginalSource as DependencyObject)?.DataContext;
        if (nodeContext is ContextMemory or MemoryConstellationNodeViewModel)
        {
            return;
        }

        _isPanningGalaxyMap = true;
        _galaxyMapPanStartPoint = e.GetPosition(scrollViewer);
        _galaxyMapPanStartOffset = new System.Windows.Point(GalaxyMapPanTransform.X, GalaxyMapPanTransform.Y);
        scrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void GalaxyMapScrollViewer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanningGalaxyMap ||
            sender is not ScrollViewer scrollViewer ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(scrollViewer);
        var delta = current - _galaxyMapPanStartPoint;
        GalaxyMapPanTransform.X = _galaxyMapPanStartOffset.X + delta.X;
        GalaxyMapPanTransform.Y = _galaxyMapPanStartOffset.Y + delta.Y;
        e.Handled = true;
    }

    private void GalaxyMapScrollViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndGalaxyMapPan(sender as ScrollViewer);
        e.Handled = true;
    }

    private void GalaxyMapScrollViewer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndGalaxyMapPan(sender as ScrollViewer);
        }
    }

    private void EndGalaxyMapPan(ScrollViewer? scrollViewer)
    {
        if (!_isPanningGalaxyMap)
        {
            return;
        }

        _isPanningGalaxyMap = false;
        scrollViewer?.ReleaseMouseCapture();
    }

    private void GalaxyTask_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ContextMemory task ||
            _viewModel == null)
        {
            return;
        }

        if (_viewModel.SelectGalaxyTaskCommand.CanExecute(task))
        {
            _viewModel.SelectGalaxyTaskCommand.Execute(task);
        }

        _draggedGalaxyTask = task;
        _draggedGalaxyPresenter = FindAncestor<ContentPresenter>(element);
        if (_draggedGalaxyPresenter == null)
        {
            _draggedGalaxyTask = null;
            return;
        }

        _dragCaptureElement = element;
        _dragStartPoint = e.GetPosition(GalaxyMapSurface);
        _dragStartOffset = new System.Windows.Point(
            Canvas.GetLeft(_draggedGalaxyPresenter),
            Canvas.GetTop(_draggedGalaxyPresenter));
        _isDraggingGalaxyTask = false;

        element.CaptureMouse();
        e.Handled = true;
    }

    private void MemoryConstellation_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemoryConstellationNodeViewModel constellation } &&
            _viewModel?.OpenMemoryConstellationCommand.CanExecute(constellation) == true)
        {
            _viewModel.OpenMemoryConstellationCommand.Execute(constellation);
            e.Handled = true;
        }
    }

    private void GalaxyTask_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggedGalaxyTask == null ||
            _draggedGalaxyPresenter == null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(GalaxyMapSurface);
        var delta = current - _dragStartPoint;
        if (!_isDraggingGalaxyTask &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isDraggingGalaxyTask = true;
        var nextX = Math.Clamp(_dragStartOffset.X + delta.X, 0, 820);
        var nextY = Math.Clamp(_dragStartOffset.Y + delta.Y, 0, 560);
        Canvas.SetLeft(_draggedGalaxyPresenter, nextX);
        Canvas.SetTop(_draggedGalaxyPresenter, nextY);
        _viewModel?.PreviewGalaxyTaskPosition(_draggedGalaxyTask, nextX, nextY);
        e.Handled = true;
    }

    private async void GalaxyTask_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragCaptureElement?.ReleaseMouseCapture();

        if (_isDraggingGalaxyTask &&
            _draggedGalaxyTask != null &&
            _draggedGalaxyPresenter != null &&
            _viewModel != null)
        {
            var x = Canvas.GetLeft(_draggedGalaxyPresenter);
            var y = Canvas.GetTop(_draggedGalaxyPresenter);
            await _viewModel.MoveGalaxyTaskAsync(_draggedGalaxyTask, x, y);
        }

        _draggedGalaxyTask = null;
        _draggedGalaxyPresenter = null;
        _dragCaptureElement = null;
        _isDraggingGalaxyTask = false;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
