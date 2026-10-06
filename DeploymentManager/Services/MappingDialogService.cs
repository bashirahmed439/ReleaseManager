using DeploymentManager.Models;
using DeploymentManager.ViewModels;
using DeploymentManager.Views;

namespace DeploymentManager.Services;

public sealed class MappingDialogService(IFolderPickerService folderPicker)
    : IMappingDialogService
{
    public DeploymentMapping? EditMapping(DeploymentMapping? mapping)
    {
        var viewModel = new MappingEditorViewModel(mapping?.Copy() ?? new DeploymentMapping(), folderPicker);
        var window = new MappingEditorWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current.MainWindow
        };

        return window.ShowDialog() == true ? viewModel.CreateMapping() : null;
    }
}