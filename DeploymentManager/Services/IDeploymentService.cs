using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IDeploymentService
{
    Task<IReadOnlyList<DeploymentResult>> DeployAsync(
        IReadOnlyList<DeploymentPlan> plans,
        string backupRoot,
        DeploymentOptions options,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}