using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IIisApplicationPoolService
{
    Task<bool> StopIfRunningAsync(DeploymentMapping mapping, CancellationToken cancellationToken = default);
    Task StartAsync(DeploymentMapping mapping, CancellationToken cancellationToken = default);
}