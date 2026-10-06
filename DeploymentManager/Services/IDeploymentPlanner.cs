using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IDeploymentPlanner
{
    Task<DeploymentPlan> CreatePlanAsync(
        DeploymentMapping mapping,
        bool hashComparisonEnabled,
        CancellationToken cancellationToken = default);
}