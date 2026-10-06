namespace DeploymentManager.Services;

public interface IRollbackConfirmationService
{
    bool Confirm(string message, string title);
}