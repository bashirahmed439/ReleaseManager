using System.Windows;

namespace DeploymentManager.Services;

public sealed class RollbackConfirmationService : IRollbackConfirmationService
{
    public bool Confirm(string message, string title) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}