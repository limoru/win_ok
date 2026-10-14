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
}