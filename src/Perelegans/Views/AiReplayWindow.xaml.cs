using System.Windows;
using MahApps.Metro.Controls;

namespace Perelegans.Views;

public partial class AiReplayWindow : MetroWindow
{
    public AiReplayWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
