using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IDeploymentPreviewService
{
    bool Confirm(IReadOnlyList<DeploymentPlan> plans);
}