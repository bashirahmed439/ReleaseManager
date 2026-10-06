using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IConfigurationService
{
    string SettingsPath { get; }
    Task<DeploymentConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DeploymentConfiguration configuration, CancellationToken cancellationToken = default);
}