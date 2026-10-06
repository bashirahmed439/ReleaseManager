using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IPackageBuilderService
{
    Task<PackageBuildResult> BuildAsync(
        PackageBuilderSettings settings,
        bool includeAllFiles,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
