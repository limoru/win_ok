using System.Globalization;
using Automation.Core.Configuration;
using Kes.Tasks.Configuration;

namespace Kes.Gui.ViewModels.Tasks;

public class TaskConfigEditorViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, TaskConfigOptionViewModel> _optionsByKey;

    public TaskConfigEditorViewModel(TaskConfigSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ModuleId = schema.ModuleId;
        DisplayName = schema.DisplayName;
        Description = schema.Description;
        Groups = schema.Groups
            .Select(group => new TaskConfigGroupViewModel(
                group.DisplayName,
                group.Options.Select(CreateOption).ToArray(),
                group.IsCollapsible,
                group.IsInitiallyExpanded))
            .ToArray();
        _optionsByKey = Groups
            .SelectMany(group => group.Options)
            .ToDictionary(option => option.Key, StringComparer.Ordinal);
    }

    public string ModuleId { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public IReadOnlyList<TaskConfigGroupViewModel> Groups { get; }
    public IReadOnlyDictionary<string, object> Snapshot
        => _optionsByKey.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.GetValue(),
            StringComparer.Ordinal);

    public object GetValue(string key)
        => _optionsByKey.TryGetValue(key, out var option)
            ? option.GetValue()
            : throw new KeyNotFoundException($"Unknown task config key: {key}");

    public void LoadSnapshot(IReadOnlyDictionary<string, object> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var option in _optionsByKey.Values)
        {
            if (snapshot.TryGetValue(option.Key, out var value))
            {
                ApplySnapshotValue(option, value);
            }
        }
    }

    private static void ApplySnapshotValue(TaskConfigOptionViewModel option, object value)
    {
        switch (option)
        {
            case BooleanConfigOptionViewModel boolean when value is bool booleanValue:
                boolean.Value = booleanValue;
                break;
            case IntegerConfigOptionViewModel integer when value is int integerValue:
                integer.ValueText = integerValue.ToString(CultureInfo.InvariantCulture);
                break;
            case TextConfigOptionViewModel text when value is string textValue:
                text.Value = textValue;
                break;
            case ChoiceConfigOptionViewModel choice when value is string choiceValue:
                choice.Value = choiceValue;
                break;
            case StringListConfigOptionViewModel list:
                list.ReplaceItems(ToTextValues(value));
                break;
            case RoutePriorityConfigOptionViewModel route:
                route.ReplaceItems(ToTextValues(value));
                break;
        }
    }

    private static IEnumerable<string> ToTextValues(object value)
        => value switch
        {
            IEnumerable<string> values => values,
            _ => []
        };

    private static TaskConfigOptionViewModel CreateOption(TaskConfigOptionDescriptor descriptor)
        => descriptor.Kind switch
        {
            TaskConfigValueKind.Boolean => new BooleanConfigOptionViewModel(descriptor),
            TaskConfigValueKind.Integer => new IntegerConfigOptionViewModel(descriptor),
            TaskConfigValueKind.Text => new TextConfigOptionViewModel(descriptor),
            TaskConfigValueKind.Choice => new ChoiceConfigOptionViewModel(descriptor),
            TaskConfigValueKind.StringList when IsRoutePriority(descriptor) => new RoutePriorityConfigOptionViewModel(descriptor),
            TaskConfigValueKind.StringList => new StringListConfigOptionViewModel(descriptor),
            _ => throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                descriptor.Kind,
                "Unsupported task config value kind.")
        };

    private static bool IsRoutePriority(TaskConfigOptionDescriptor descriptor)
        => descriptor.Key is "第一层路线优先级" or "第二层路线优先级";
}

public sealed class TaskConfigGroupViewModel : ObservableObject
{
    private bool _isExpanded;

    public TaskConfigGroupViewModel(
        string displayName,
        IReadOnlyList<TaskConfigOptionViewModel> options,
        bool isCollapsible = true,
        bool isInitiallyExpanded = true)
    {
        DisplayName = displayName;
        Options = options;
        IsCollapsible = isCollapsible;
        _isExpanded = isInitiallyExpanded;
    }

    public string DisplayName { get; }
    public IReadOnlyList<TaskConfigOptionViewModel> Options { get; }
    public bool IsCollapsible { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}

public sealed class ChaosModeSettingsViewModel : TaskConfigEditorViewModel
{
    private readonly IReadOnlyDictionary<string, TaskConfigGroupViewModel> _contextGroupsBySeasonValue;

    public ChaosModeSettingsViewModel()
        : base(ChaosModeConfigSchema.Instance)
    {
        SeasonSelector = FindOption<ChoiceConfigOptionViewModel>(ChaosModeConfigSchema.SeasonSelectorKey);
        ContentGroups = Groups
            .Where(group => group.DisplayName != ChaosModeConfigSchema.SeasonSelectorGroupName
                            && group.DisplayName != ChaosModeConfigSchema.EquipmentGroupName
                            && group.DisplayName != ChaosModeConfigSchema.EngravingGroupName
                            && group.DisplayName != ChaosModeConfigSchema.BrokenLightAndClawGroupName)
            .ToArray();
        _contextGroupsBySeasonValue = new Dictionary<string, TaskConfigGroupViewModel>(StringComparer.Ordinal)
        {
            [ChaosModeConfigSchema.NoSeasonContent] = Groups.Single(
                group => group.DisplayName == ChaosModeConfigSchema.EquipmentGroupName),
            [ChaosModeConfigSchema.SongInGalaxySeason] = Groups.Single(
                group => group.DisplayName == ChaosModeConfigSchema.EngravingGroupName),
            [ChaosModeConfigSchema.BrokenLightAndClawSeason] = Groups.Single(
                group => group.DisplayName == ChaosModeConfigSchema.BrokenLightAndClawGroupName)
        };

        SeasonSelector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChoiceConfigOptionViewModel.Value))
            {
                OnPropertyChanged(nameof(VisibleContextGroups));
                OnPropertyChanged(nameof(IsEngravingSeasonSelected));
            }
        };
    }

    public string DefaultProfileName
    {
        get
        {
            var character = GetValue("刷存档主战员") as string;
            var season = GetValue(ChaosModeConfigSchema.SeasonSelectorKey) as string;
            return string.Join(
                " · ",
                new[] { character, season }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    public ChoiceConfigOptionViewModel SeasonSelector { get; }
    public IReadOnlyList<TaskConfigGroupViewModel> ContentGroups { get; }

    public bool IsEngravingSeasonSelected
        => string.Equals(
            SeasonSelector.Value,
            ChaosModeConfigSchema.SongInGalaxySeason,
            StringComparison.Ordinal);

    public IReadOnlyList<TaskConfigGroupViewModel> VisibleContextGroups
        => _contextGroupsBySeasonValue.TryGetValue(SeasonSelector.Value, out var group)
            ? [group]
            : [];

    private T FindOption<T>(string key)
        where T : TaskConfigOptionViewModel
        => Groups
            .SelectMany(group => group.Options)
            .OfType<T>()
            .Single(option => option.Key == key);
}