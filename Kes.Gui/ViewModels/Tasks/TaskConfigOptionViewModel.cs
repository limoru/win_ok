using System.Collections.ObjectModel;
using System.Globalization;
using Automation.Core.Configuration;
using Kes.Gui.ViewModels;

namespace Kes.Gui.ViewModels.Tasks;

public abstract class TaskConfigOptionViewModel : ObservableObject
{
    protected TaskConfigOptionViewModel(TaskConfigOptionDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public TaskConfigOptionDescriptor Descriptor { get; }
    public string Key => Descriptor.Key;
    public string DisplayName => Descriptor.DisplayName;
    public string Description => Descriptor.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public abstract object GetValue();
}

public sealed class BooleanConfigOptionViewModel : TaskConfigOptionViewModel
{
    private bool _value;

    public BooleanConfigOptionViewModel(TaskConfigOptionDescriptor descriptor)
        : base(descriptor)
    {
        _value = descriptor.DefaultValue is bool value && value;
    }

    public bool Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public override object GetValue() => Value;
}

public sealed class IntegerConfigOptionViewModel : TaskConfigOptionViewModel
{
    private int _value;
    private string _valueText;
    private bool _hasError;

    public IntegerConfigOptionViewModel(TaskConfigOptionDescriptor descriptor)
        : base(descriptor)
    {
        _value = Convert.ToInt32(descriptor.DefaultValue, CultureInfo.InvariantCulture);
        _valueText = _value.ToString(CultureInfo.InvariantCulture);
    }

    public int Value => _value;
    public int? Minimum => Descriptor.Minimum;
    public int? Maximum => Descriptor.Maximum;

    public string ValueText
    {
        get => _valueText;
        set
        {
            if (!SetProperty(ref _valueText, value))
            {
                return;
            }

            var isValid = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                          && (!Minimum.HasValue || parsed >= Minimum.Value)
                          && (!Maximum.HasValue || parsed <= Maximum.Value);
            if (isValid)
            {
                if (SetProperty(ref _value, parsed, nameof(Value)))
                {
                    OnPropertyChanged(nameof(ValueText));
                }

                HasError = false;
            }
            else
            {
                HasError = true;
            }
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public override object GetValue() => Value;
}

public sealed class TextConfigOptionViewModel : TaskConfigOptionViewModel
{
    private string _value;

    public TextConfigOptionViewModel(TaskConfigOptionDescriptor descriptor)
        : base(descriptor)
    {
        _value = descriptor.DefaultValue as string ?? string.Empty;
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public override object GetValue() => Value;
}

public sealed class ChoiceConfigOptionViewModel : TaskConfigOptionViewModel
{
    private string _value;

    public ChoiceConfigOptionViewModel(TaskConfigOptionDescriptor descriptor)
        : base(descriptor)
    {
        Choices = descriptor.Choices ?? [];
        _value = descriptor.DefaultValue as string ?? Choices.FirstOrDefault() ?? string.Empty;
    }

    public IReadOnlyList<string> Choices { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _value, value);
            }
        }
    }

    public override object GetValue() => Value;
}

public sealed class StringListConfigOptionViewModel : TaskConfigOptionViewModel
{
    public StringListConfigOptionViewModel(TaskConfigOptionDescriptor descriptor)
        : base(descriptor)
    {
        Items = new ObservableCollection<StringListItemViewModel>();
        ReplaceItems(descriptor.DefaultValue as IEnumerable<string> ?? []);
    }

    public ObservableCollection<StringListItemViewModel> Items { get; }
    public int ItemCount => Items.Count;
    public string Summary
    {
        get
        {
            if (Items.Count == 0)
            {
                return "未配置";
            }

            var preview = string.Join(" / ", Items.Take(2).Select(item => item.Value));
            return Items.Count > 2
                ? $"{preview} 等 {Items.Count} 项"
                : preview;
        }
    }

    public void ReplaceItems(IEnumerable<string> values)
    {
        Items.Clear();
        foreach (var value in values)
        {
            Items.Add(new StringListItemViewModel(value));
        }

        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(Summary));
    }

    public override object GetValue() => Items.Select(item => item.Value).ToArray();
}

public sealed class StringListItemViewModel : ObservableObject
{
    private string _value;

    public StringListItemViewModel(string value)
    {
        _value = value;
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}
public sealed class RoutePriorityConfigOptionViewModel : TaskConfigOptionViewModel
{
    public RoutePriorityConfigOptionViewModel(TaskConfigOptionDescriptor descriptor)
        : base(descriptor)
    {
        var values = descriptor.DefaultValue as IEnumerable<string> ?? [];
        Items = new ObservableCollection<RoutePriorityItemViewModel>(
            values.Select(value => new RoutePriorityItemViewModel(value)));
    }

    public ObservableCollection<RoutePriorityItemViewModel> Items { get; }

    public void ReplaceItems(IEnumerable<string> values)
    {
        Items.Clear();
        foreach (var value in values)
        {
            Items.Add(new RoutePriorityItemViewModel(value));
        }
    }

    public override object GetValue() => Items.Select(item => item.Value).ToArray();
}

public sealed class RoutePriorityItemViewModel
{
    public RoutePriorityItemViewModel(string value)
    {
        Value = value;
    }

    public string Value { get; }
}