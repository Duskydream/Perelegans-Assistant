using System;
using System.Windows;
using System.Windows.Media;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Services;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class SettingsWindow : MetroWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        try
        {
            vm.SaveCommand.Execute(null);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            await this.ShowMessageAsync(TranslationService.Instance["Msg_ErrorTitle"], ex.Message);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void AppearanceNav_Click(object sender, RoutedEventArgs e)
    {
        ScrollToSection(AppearanceSection);
    }

    private void AiNav_Click(object sender, RoutedEventArgs e)
    {
        ScrollToSection(AiSection);
    }

    private void MemoryNav_Click(object sender, RoutedEventArgs e)
    {
        ScrollToSection(MemorySection);
    }

    private void SystemNav_Click(object sender, RoutedEventArgs e)
    {
        ScrollToSection(SystemSection);
    }

    private void DataNav_Click(object sender, RoutedEventArgs e)
    {
        ScrollToSection(DataSection);
    }

    private void ScrollToSection(FrameworkElement section)
    {
        var position = section.TransformToAncestor(SettingsScrollViewer)
            .Transform(new System.Windows.Point(0, 0));

        SettingsScrollViewer.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset + position.Y);
    }
}
