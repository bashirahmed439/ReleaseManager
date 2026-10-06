using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IDeploymentRollbackService
{
    Task<IReadOnlyList<RollbackApplicationResult>> RollbackAsync(
        LastDeploymentRecord deployment,
        string? applicationName = null,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}