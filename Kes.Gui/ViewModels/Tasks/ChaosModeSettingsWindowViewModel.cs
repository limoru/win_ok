using System.Collections.ObjectModel;
using Kes.Tasks.Configuration;

namespace Kes.Gui.ViewModels.Tasks;

public sealed class ChaosModeSettingsWindowViewModel : ObservableObject
{
    private readonly Action<TaskConfigProfile>? _onProfileSaved;
    private TaskConfigProfile? _selectedProfile;
    private string _profileName;
    private string _saveStatusText = string.Empty;

    public ChaosModeSettingsWindowViewModel(
        ChaosModeSettingsViewModel settings,
        TaskConfigProfileStore profileStore,
        TaskConfigProfile? selectedProfile = null,
        Action<TaskConfigProfile>? onProfileSaved = null)
    {
        Settings = settings;
        ProfileStore = profileStore;
        _onProfileSaved = onProfileSaved;
        _selectedProfile = selectedProfile;
        _profileName = settings.DefaultProfileName;
        SaveProfileCommand = new RelayCommand(SaveProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => HasSelectedProfile);
    }

    public ChaosModeSettingsViewModel Settings { get; }
    public TaskConfigProfileStore ProfileStore { get; }
    public ObservableCollection<TaskConfigProfile> Profiles => ProfileStore.Profiles;
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }

    public TaskConfigProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedProfile));
            DeleteProfileCommand.RaiseCanExecuteChanged();
            if (value is not null)
            {
                ProfileName = value.Name;
            }
        }
    }

    public bool HasSelectedProfile => SelectedProfile is not null;

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public string SaveStatusText
    {
        get => _saveStatusText;
        private set => SetProperty(ref _saveStatusText, value);
    }

    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            SaveStatusText = "请输入配置方案名称。";
            return;
        }

        try
        {
            var profile = ProfileStore.Save(
                "ChaosMode",
                ProfileName,
                Settings.Snapshot);
            SelectedProfile = profile;
            ProfileName = profile.Name;
            SaveStatusText = $"已保存：{profile.Name}";
            _onProfileSaved?.Invoke(profile);
        }
        catch (Exception exception)
        {
            SaveStatusText = $"保存失败：{exception.Message}";
        }
    }

    private void DeleteProfile()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            SaveStatusText = "请先选择要删除的方案。";
            return;
        }

        try
        {
            if (!ProfileStore.Delete(profile.Id))
            {
                SaveStatusText = "方案不存在或已经删除。";
                return;
            }

            SelectedProfile = null;
            ProfileName = Settings.DefaultProfileName;
            SaveStatusText = $"已删除：{profile.Name}";
        }
        catch (Exception exception)
        {
            SaveStatusText = $"删除失败：{exception.Message}";
        }
    }
}