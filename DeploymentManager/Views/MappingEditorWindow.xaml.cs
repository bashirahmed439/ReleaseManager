using System.Windows;
using DeploymentManager.ViewModels;

namespace DeploymentManager.Views;

public partial class MappingEditorWindow : Window
{
    public MappingEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MappingEditorViewModel viewModel)
            {
                viewModel.OnRequestClose += accepted => DialogResult = accepted;
            }
        };
    }
}