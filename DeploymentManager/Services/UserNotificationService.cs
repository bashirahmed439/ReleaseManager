using System.Windows;

namespace DeploymentManager.Services;

public sealed class UserNotificationService : IUserNotificationService
{
    public void ShowInformation(string title, string message) =>
        Show(title, message, MessageBoxImage.Information);

    public void ShowWarning(string title, string message) =>
        Show(title, message, MessageBoxImage.Warning);

    public void ShowError(string title, string message) =>
        Show(title, message, MessageBoxImage.Error);

    private static void Show(string title, string message, MessageBoxImage image) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, image);
}