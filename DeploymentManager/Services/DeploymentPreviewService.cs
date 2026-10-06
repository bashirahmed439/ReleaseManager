using DeploymentManager.Models;
using DeploymentManager.ViewModels;
using DeploymentManager.Views;

namespace DeploymentManager.Services;

public sealed class DeploymentPreviewService : IDeploymentPreviewService
{
    public bool Confirm(IReadOnlyList<DeploymentPlan> plans)
    {
        var window = new DeploymentPreviewWindow
        {
            DataContext = new DeploymentPreviewViewModel(plans),
            Owner = System.Windows.Application.Current.MainWindow
        };

        return window.ShowDialog() == true;
    }
}