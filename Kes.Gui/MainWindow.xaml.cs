using System.Windows;
using Kes.Gui.ViewModels;
using Kes.Gui.ViewModels.Tasks;
using Kes.Gui.Views;
using Kes.Tasks.Configuration;

namespace Kes.Gui;

public partial class MainWindow : Window
{
    private bool _loaded;
    private DeveloperPreviewWindow? _developerPreviewWindow;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainWindowViewModel();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public MainWindowViewModel ViewModel { get; }

    private void OpenChaosModeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var session = ViewModel.SelectedSession;
        OpenChaosModeSettings(
            session?.ChaosModeSettings ?? ViewModel.ChaosModeSettings,
            session?.SelectedChaosModeProfile,
            session is null ? null : profile => session.SelectedChaosModeProfile = profile);
    }

    private void OpenSessionTaskSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionViewModel session })
        {
            return;
        }

        ViewModel.SelectedSession = session;
        if (session.SelectedTaskPlanOption.Descriptor?.ConfigViewId == "ChaosModeConfig")
        {
            OpenChaosModeSettings(
                session.ChaosModeSettings,
                session.SelectedChaosModeProfile,
                profile => session.SelectedChaosModeProfile = profile);
        }
    }

    private void OpenChaosModeSettings(
        ChaosModeSettingsViewModel settings,
        TaskConfigProfile? selectedProfile = null,
        Action<TaskConfigProfile>? onProfileSaved = null)
    {
        var settingsWindow = new ChaosModeSettingsWindow(
            settings,
            ViewModel.ConfigProfileStore,
            selectedProfile,
            onProfileSaved)
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await ViewModel.InitializeAsync();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _developerPreviewWindow?.Close();
        _developerPreviewWindow = null;
        await ViewModel.DisposeAsync();
    }

    private void DeveloperPreview_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var position = e.GetPosition(DeveloperPreviewSurface);
        ViewModel.UpdateDeveloperPointer(
            position.X,
            position.Y,
            DeveloperPreviewSurface.ActualWidth,
            DeveloperPreviewSurface.ActualHeight);
    }

    private void DeveloperPreview_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => ViewModel.ClearDeveloperPointer();

    private void DeveloperPreviewOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasDeveloperPreviewImage)
        {
            return;
        }

        if (_developerPreviewWindow is { IsVisible: true } existingWindow)
        {
            if (existingWindow.WindowState == WindowState.Minimized)
            {
                existingWindow.WindowState = WindowState.Maximized;
            }

            existingWindow.Activate();
            return;
        }

        var previewWindow = new DeveloperPreviewWindow(ViewModel)
        {
            Owner = this
        };
        previewWindow.Closed += (_, _) => _developerPreviewWindow = null;

        _developerPreviewWindow = previewWindow;
        previewWindow.Show();
        previewWindow.Activate();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}