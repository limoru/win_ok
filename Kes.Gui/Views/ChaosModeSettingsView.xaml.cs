using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kes.Gui.ViewModels.Tasks;

namespace Kes.Gui.Views;

public partial class ChaosModeSettingsView : UserControl
{
    private Point _routeDragStart;
    private RoutePriorityItemViewModel? _draggedRouteItem;
    private ListBoxItem? _dropTargetItem;
    private bool _dropAfter;

    public ChaosModeSettingsView()
    {
        InitializeComponent();
    }

    private void ConfigGroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button
            {
                DataContext: TaskConfigGroupViewModel group
            } button
            && group.IsCollapsible)
        {
            group.IsExpanded = !group.IsExpanded;
        }
    }
    private void EditStringListButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: StringListConfigOptionViewModel option
            })
        {
            return;
        }

        var editor = new StringListEditorWindow(option)
        {
            Owner = Window.GetWindow(this)
        };
        editor.ShowDialog();
    }
    private void RoutePriorityList_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox
            || ItemsControl.ContainerFromElement(
                listBox,
                e.OriginalSource as DependencyObject) is not ListBoxItem
                {
                    DataContext: RoutePriorityItemViewModel item
                } container)
        {
            _draggedRouteItem = null;
            return;
        }

        _routeDragStart = e.GetPosition(listBox);
        _draggedRouteItem = item;
        listBox.SelectedItem = item;
    }

    private void RoutePriorityList_PreviewMouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _draggedRouteItem is null
            || sender is not ListBox listBox)
        {
            return;
        }

        var position = e.GetPosition(listBox);
        if (Math.Abs(position.X - _routeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _routeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedItem = _draggedRouteItem;
        try
        {
            DragDrop.DoDragDrop(listBox, draggedItem, DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicators(listBox);
            listBox.SelectedItem = null;
            _draggedRouteItem = null;
            _dropTargetItem = null;
            _dropAfter = false;
        }
    }

    private void RoutePriorityList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox
            || !e.Data.GetDataPresent(typeof(RoutePriorityItemViewModel)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ClearDropIndicators(listBox);
        _dropTargetItem = ItemsControl.ContainerFromElement(
            listBox,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        _dropAfter = true;

        if (_dropTargetItem is not null)
        {
            var pointer = e.GetPosition(_dropTargetItem);
            _dropAfter = pointer.X >= _dropTargetItem.ActualWidth / 2;
            _dropTargetItem.Tag = _dropAfter ? "After" : "Before";
        }
        else if (listBox.Items.Count > 0)
        {
            _dropTargetItem = listBox.ItemContainerGenerator.ContainerFromIndex(listBox.Items.Count - 1) as ListBoxItem;
            if (_dropTargetItem is not null)
            {
                _dropTargetItem.Tag = "After";
            }
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void RoutePriorityList_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            ClearDropIndicators(listBox);
        }
    }

    private void RoutePriorityList_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox
            {
                DataContext: RoutePriorityConfigOptionViewModel option
            } listBox
            || e.Data.GetData(typeof(RoutePriorityItemViewModel)) is not RoutePriorityItemViewModel draggedItem)
        {
            return;
        }

        var oldIndex = option.Items.IndexOf(draggedItem);
        if (oldIndex < 0)
        {
            return;
        }

        var targetItem = _dropTargetItem
                         ?? ItemsControl.ContainerFromElement(
                             listBox,
                             e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = targetItem?.DataContext as RoutePriorityItemViewModel;
        var newIndex = option.Items.Count - 1;

        if (target is not null)
        {
            var targetIndex = option.Items.IndexOf(target);
            if (targetIndex >= 0)
            {
                newIndex = targetIndex + (_dropAfter ? 1 : 0);
                if (oldIndex < newIndex)
                {
                    newIndex--;
                }
            }
        }

        newIndex = Math.Clamp(newIndex, 0, option.Items.Count - 1);
        if (newIndex != oldIndex)
        {
            option.Items.Move(oldIndex, newIndex);
            AnimateMovedItem(listBox, draggedItem);
        }

        ClearDropIndicators(listBox);
        listBox.SelectedItem = null;
        _dropTargetItem = null;
        _dropAfter = false;
        e.Handled = true;
    }

    private void AnimateMovedItem(ListBox listBox, RoutePriorityItemViewModel item)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                if (listBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                {
                    return;
                }

                container.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase
                        {
                            EasingMode = EasingMode.EaseOut
                        }
                    });
            });
    }

    private static void ClearDropIndicators(ListBox listBox)
    {
        for (var index = 0; index < listBox.Items.Count; index++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
            {
                container.Tag = null;
            }
        }
    }
}