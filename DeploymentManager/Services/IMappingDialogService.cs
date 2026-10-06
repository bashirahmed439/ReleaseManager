using DeploymentManager.Models;

namespace DeploymentManager.Services;

public interface IMappingDialogService
{
    DeploymentMapping? EditMapping(DeploymentMapping? mapping);
}