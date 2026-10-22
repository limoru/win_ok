using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Automation.Core.Frames;
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
        _profileName = selectedProfile?.Name ?? settings.DefaultProfileName;
        if (selectedProfile is not null)
        {
            Settings.LoadSnapshot(selectedProfile.Values);
        }

        SaveProfileCommand = new RelayCommand(SaveProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => HasSelectedProfile);
        RemoveAvatarCommand = new RelayCommand(RemoveAvatar, parameter => parameter is TargetMemberAvatarPreviewViewModel);
        RefreshAvatarPreviews();
    }

    public ChaosModeSettingsViewModel Settings { get; }
    public TaskConfigProfileStore ProfileStore { get; }
    public ObservableCollection<TaskConfigProfile> Profiles => ProfileStore.Profiles;
    public ObservableCollection<TargetMemberAvatarPreviewViewModel> AvatarPreviews { get; } = [];
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand RemoveAvatarCommand { get; }

    public TaskConfigProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            if (value is not null)
            {
                Settings.LoadSnapshot(value.Values);
                ProfileName = value.Name;
            }

            OnPropertyChanged(nameof(HasSelectedProfile));
            OnPropertyChanged(nameof(CanAddAvatar));
            DeleteProfileCommand.RaiseCanExecuteChanged();
            RefreshAvatarPreviews();
        }
    }

    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool CanAddAvatar => HasSelectedProfile && !string.IsNullOrWhiteSpace(GetTargetMemberName());
    public bool HasNoAvatars => AvatarPreviews.Count == 0;
    public string AvatarCountText => string.Concat(
        AvatarPreviews.Count,
        "/",
        TaskConfigProfileStore.MaxRuntimeTemplatesPerFeature);

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

    public void AddAvatarFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var selectedProfile = SelectedProfile;
        var targetName = GetTargetMemberName();
        if (selectedProfile is null || string.IsNullOrWhiteSpace(targetName))
        {
            SaveStatusText = "请先选择任务方案并填写刷存档主战员。";
            return;
        }

        var added = 0;
        var duplicates = 0;
        var failed = 0;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var avatar = LoadAvatarFrame(path);
                if (TargetMemberAvatarCatalog.AddAvatar(
                        ProfileStore,
                        selectedProfile.Id,
                        targetName,
                        avatar))
                {
                    added++;
                }
                else
                {
                    duplicates++;
                }
            }
            catch
            {
                failed++;
            }
        }

        RefreshAvatarPreviews();
        SaveStatusText = failed > 0
            ? $"已添加 {added} 个，重复 {duplicates} 个，失败 {failed} 个。"
            : duplicates > 0
                ? $"已添加 {added} 个，重复 {duplicates} 个。"
                : $"已添加 {added} 个头像。";
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
                Settings.Snapshot,
                SelectedProfile?.Id);
            SelectedProfile = profile;
            ProfileName = profile.Name;
            RefreshAvatarPreviews();
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
            RefreshAvatarPreviews();
            SaveStatusText = $"已删除：{profile.Name}";
        }
        catch (Exception exception)
        {
            SaveStatusText = $"删除失败：{exception.Message}";
        }
    }

    private void RemoveAvatar(object? parameter)
    {
        if (SelectedProfile is null
            || string.IsNullOrWhiteSpace(GetTargetMemberName())
            || parameter is not TargetMemberAvatarPreviewViewModel preview)
        {
            return;
        }

        try
        {
            if (TargetMemberAvatarCatalog.RemoveAvatarAt(
                    ProfileStore,
                    SelectedProfile.Id,
                    GetTargetMemberName(),
                    preview.Index))
            {
                SaveStatusText = $"已删除头像 {preview.Index + 1}。";
                RefreshAvatarPreviews();
            }
        }
        catch (Exception exception)
        {
            SaveStatusText = $"删除头像失败：{exception.Message}";
        }
    }

    private void RefreshAvatarPreviews()
    {
        AvatarPreviews.Clear();
        if (SelectedProfile is { } profile)
        {
            var targetName = GetTargetMemberName();
            foreach (var template in TargetMemberAvatarCatalog.Load(profile, targetName))
            {
                AvatarPreviews.Add(new TargetMemberAvatarPreviewViewModel(
                    template.Index,
                    CreatePreview(template)));
            }
        }

        OnPropertyChanged(nameof(HasNoAvatars));
        OnPropertyChanged(nameof(AvatarCountText));
        OnPropertyChanged(nameof(CanAddAvatar));
        RemoveAvatarCommand.RaiseCanExecuteChanged();
    }

    private string GetTargetMemberName()
        => (Settings.GetValue("刷存档主战员") as string)?.Trim() ?? string.Empty;

    private static BgraFrame LoadAvatarFrame(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("图片不包含可用帧。");
        }

        var converted = new FormatConvertedBitmap(
            decoder.Frames[0],
            PixelFormats.Bgra32,
            null,
            0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return new BgraFrame(converted.PixelWidth, converted.PixelHeight, pixels);
    }

    private static ImageSource CreatePreview(TargetMemberAvatarTemplate template)
    {
        var image = BitmapSource.Create(
            template.Width,
            template.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            template.Pixels,
            template.Width * 4);
        image.Freeze();
        return image;
    }
}

public sealed class TargetMemberAvatarPreviewViewModel
{
    public TargetMemberAvatarPreviewViewModel(int index, ImageSource preview)
    {
        Index = index;
        Preview = preview;
    }

    public int Index { get; }
    public ImageSource Preview { get; }
    public string Label => $"头像 {Index + 1}";
}
