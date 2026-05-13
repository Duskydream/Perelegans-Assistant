using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class MainWindow : MetroWindow
{
    private MainViewModel? _viewModel;

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
}
