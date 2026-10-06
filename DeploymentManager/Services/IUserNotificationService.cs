namespace DeploymentManager.Services;

public interface IUserNotificationService
{
    void ShowInformation(string title, string message);
    void ShowWarning(string title, string message);
    void ShowError(string title, string message);
}