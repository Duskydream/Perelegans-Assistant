using System.Windows;
using System.Windows.Input;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class FloatingPetWindow : Window
{
    public FloatingPetWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
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
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
