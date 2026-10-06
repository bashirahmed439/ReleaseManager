using System.Collections.ObjectModel;
using DeploymentManager.Models;
using DeploymentManager.Services;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.ViewModels;

public sealed class PackageBuilderViewModel : ObservableObject
{
    private readonly IPackageBuilderService _packageBuilder;
    private readonly IFolderPickerService _folderPicker;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<PackageBuilderViewModel> _logger;
    private CancellationTokenSource? _cancellation;
    private string _solutionFolder = string.Empty;
    private string _outputFolder = string.Empty;
    private string _excludePatterns = string.Empty;
    private bool _isBusy;
    private string _statusMessage = "Publish your projects in Visual Studio, then package files published in the last hour.";
    private string _lastPackageFolder = string.Empty;
    private PackageProjectResult? _selectedResult;

    public PackageBuilderViewModel(
        IPackageBuilderService packageBuilder,
        IFolderPickerService folderPicker,
        IUserNotificationService notifications,
        ILogger<PackageBuilderViewModel> logger)
    {
        _packageBuilder = packageBuilder;
        _folderPicker = folderPicker;
        _notifications = notifications;
        _logger = logger;
        BrowseSolutionCommand = new RelayCommand(() => Browse(SolutionFolder, "Select the solution folder", path => SolutionFolder = path));
        BrowseOutputCommand = new RelayCommand(() => Browse(OutputFolder, "Select the folder for deployable packages", path => OutputFolder = path));
        CreatePackageCommand = new AsyncRelayCommand(CreatePackageAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
    }

    public Func<Task>? SaveSettingsAsync { get; set; }
    public ObservableCollection<PackageProjectResult> Results { get; } = [];
    public RelayCommand BrowseSolutionCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public AsyncRelayCommand CreatePackageCommand { get; }
    public RelayCommand CancelCommand { get; }
    public string SolutionFolder { get => _solutionFolder; set => SetProperty(ref _solutionFolder, value); }
    public string OutputFolder { get => _outputFolder; set => SetProperty(ref _outputFolder, value); }
    public string ExcludePatterns { get => _excludePatterns; set => SetProperty(ref _excludePatterns, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string LastPackageFolder { get => _lastPackageFolder; private set => SetProperty(ref _lastPackageFolder, value); }
    public PackageProjectResult? SelectedResult { get => _selectedResult; set => SetProperty(ref _selectedResult, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CreatePackageCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public void Load(PackageBuilderSettings settings)
    {
        SolutionFolder = settings.SolutionFolder;
        OutputFolder = settings.OutputFolder;
        ExcludePatterns = settings.ExcludePatterns;
    }

    public PackageBuilderSettings ToSettings() => new()
    {
        SolutionFolder = SolutionFolder.Trim(),
        OutputFolder = OutputFolder.Trim(),
        ExcludePatterns = ExcludePatterns.Trim()
    };

    private void Browse(string currentPath, string title, Action<string> apply)
    {
        var selected = _folderPicker.PickFolder(currentPath, title);
        if (selected is not null)
        {
            apply(selected);
            _ = PersistAsync();
        }
    }

    private async Task PersistAsync()
    {
        if (SaveSettingsAsync is not null)
        {
            await SaveSettingsAsync();
        }
    }

    private async Task CreatePackageAsync()
    {
        IsBusy = true;
        Results.Clear();
        SelectedResult = null;
        StatusMessage = "Scanning publish folders...";
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        try
        {
            await PersistAsync();
            var settings = ToSettings();
            var progress = new Progress<string>(message => StatusMessage = message);
            var result = await Task.Run(() => _packageBuilder.BuildAsync(settings, includeAllFiles: false, progress, token), token);

            foreach (var project in result.Projects)
            {
                Results.Add(project);
            }

            SelectedResult = Results.FirstOrDefault(project => project.Files.Count > 0);
            if (result.PackageRoot is null)
            {
                StatusMessage = "No files were published in the last hour. Publish again, then create a package.";
                _notifications.ShowWarning("No package created", StatusMessage);
            }
            else
            {
                LastPackageFolder = result.PackageRoot;
                StatusMessage = $"Created deployable folders with {result.TotalFiles} file(s) published in the last hour.";
                _notifications.ShowInformation("Package created", StatusMessage);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Package creation cancelled. No package folder was kept.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Package creation failed");
            StatusMessage = $"Package creation failed: {exception.Message}";
            _notifications.ShowError("Package creation failed", exception.Message);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }
}
