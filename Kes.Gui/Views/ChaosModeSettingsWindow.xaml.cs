using Microsoft.Win32;
using System.Windows;
using Kes.Gui.ViewModels.Tasks;
using Kes.Tasks.Configuration;

namespace Kes.Gui.Views;

public partial class ChaosModeSettingsWindow : Window
{
    public ChaosModeSettingsWindow(
        ChaosModeSettingsViewModel settings,
        TaskConfigProfileStore profileStore,
        TaskConfigProfile? selectedProfile = null,
        Action<TaskConfigProfile>? onProfileSaved = null)
    {
        InitializeComponent();
        DataContext = new ChaosModeSettingsWindowViewModel(
            settings,
            profileStore,
            selectedProfile,
            onProfileSaved);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void AddAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChaosModeSettingsWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择干员头像",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            viewModel.AddAvatarFiles(dialog.FileNames);
        }
    }
}
