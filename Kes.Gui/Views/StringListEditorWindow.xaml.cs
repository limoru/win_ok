using System.Windows;
using Kes.Gui.ViewModels.Tasks;

namespace Kes.Gui.Views;

public partial class StringListEditorWindow : Window
{
    private readonly StringListEditorViewModel _viewModel;

    public StringListEditorWindow(StringListConfigOptionViewModel source)
    {
        InitializeComponent();
        _viewModel = new StringListEditorViewModel(source);
        DataContext = _viewModel;
        Title = $"编辑列表 - {_viewModel.DisplayName}";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Apply();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}