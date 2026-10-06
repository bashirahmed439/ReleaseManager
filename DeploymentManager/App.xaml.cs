using DeploymentManager.Services;
using DeploymentManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace DeploymentManager;

public partial class App : Application
{
	private IHost? _host;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		try
		{
			_host = Host.CreateDefaultBuilder()
				.ConfigureLogging(logging => logging.AddDebug())
				.ConfigureServices((_, services) =>
				{
					services.AddSingleton<IConfigurationService, JsonConfigurationService>();
					services.AddSingleton<IFolderPickerService, FolderPickerService>();
					services.AddSingleton<IMappingDialogService, MappingDialogService>();
					services.AddSingleton<IIniConfigurationMerger, IniConfigurationMerger>();
					services.AddSingleton<IDeploymentPlanner, DeploymentPlanner>();
					services.AddSingleton<IIisApplicationPoolService, PowerShellIisApplicationPoolService>();
					services.AddSingleton<IJavaScriptScaffoldingService, JavaScriptScaffoldingService>();
					services.AddSingleton<IDeploymentService, DeploymentService>();
					services.AddSingleton<IDeploymentRollbackService, DeploymentRollbackService>();
					services.AddSingleton<IRollbackConfirmationService, RollbackConfirmationService>();
					services.AddSingleton<IUserNotificationService, UserNotificationService>();
					services.AddSingleton<IPackageBuilderService, PackageBuilderService>();
					services.AddSingleton<PackageBuilderViewModel>();
					services.AddSingleton<IDeploymentPreviewService, DeploymentPreviewService>();
					services.AddSingleton<MainViewModel>();
					services.AddSingleton<MainWindow>();
				})
				.Build();

			await _host.StartAsync();
			var window = _host.Services.GetRequiredService<MainWindow>();
			MainWindow = window;
			await window.InitializeAsync();
			window.Show();
		}
		catch (Exception exception)
		{
			MessageBox.Show(exception.Message, "Deployment Manager could not start", MessageBoxButton.OK, MessageBoxImage.Error);
			Shutdown(1);
		}
	}

	protected override async void OnExit(ExitEventArgs e)
	{
		if (_host is not null)
		{
			await _host.StopAsync();
			_host.Dispose();
		}

		base.OnExit(e);
	}
}

