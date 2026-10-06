using System.Windows;

namespace DeploymentManager.Views;

public partial class DeploymentPreviewWindow : Window
{
    public DeploymentPreviewWindow()
    {
        InitializeComponent();
    }

    private void Deploy_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}