using System.Collections.ObjectModel;
using Kes.Gui.ViewModels;

namespace Kes.Gui.ViewModels.Tasks;

public sealed class StringListEditorViewModel : ObservableObject
{
    private readonly StringListConfigOptionViewModel _source;

    public StringListEditorViewModel(StringListConfigOptionViewModel source)
    {
        _source = source;
        DisplayName = source.DisplayName;
        Description = source.Description;
        Items = new ObservableCollection<StringListItemViewModel>(
            source.Items.Select(item => new StringListItemViewModel(item.Value)));
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ItemCount));
            RaiseCommandStates();
        };

        AddCommand = new RelayCommand(AddItem);
        RemoveCommand = new RelayCommand(
            RemoveItem,
            item => item is StringListItemViewModel);
        MoveUpCommand = new RelayCommand(
            item => MoveItem(item, -1),
            item => item is StringListItemViewModel target && Items.IndexOf(target) > 0);
        MoveDownCommand = new RelayCommand(
            item => MoveItem(item, 1),
            item => item is StringListItemViewModel target
                    && Items.IndexOf(target) is var index
                    && index >= 0
                    && index < Items.Count - 1);
    }

    public string DisplayName { get; }
    public string Description { get; }
    public ObservableCollection<StringListItemViewModel> Items { get; }
    public int ItemCount => Items.Count;

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    public void Apply()
        => _source.ReplaceItems(Items.Select(item => item.Value));

    private void AddItem()
        => Items.Add(new StringListItemViewModel(string.Empty));

    private void RemoveItem(object? parameter)
    {
        if (parameter is StringListItemViewModel item)
        {
            Items.Remove(item);
        }
    }

    private void MoveItem(object? parameter, int offset)
    {
        if (parameter is not StringListItemViewModel item)
        {
            return;
        }

        var oldIndex = Items.IndexOf(item);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Items.Count)
        {
            return;
        }

        Items.Move(oldIndex, newIndex);
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        RemoveCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }
}