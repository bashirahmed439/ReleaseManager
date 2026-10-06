using System.Windows;
using DeploymentManager.ViewModels;

namespace DeploymentManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public Task InitializeAsync() => _viewModel.InitializeAsync();
}