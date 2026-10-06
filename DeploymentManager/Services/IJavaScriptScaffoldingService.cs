using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IJavaScriptScaffoldingService
{
    Task ProcessAsync(
        string sourceFile,
        string destinationFile,
        DeploymentMapping mapping,
        CancellationToken cancellationToken);
}