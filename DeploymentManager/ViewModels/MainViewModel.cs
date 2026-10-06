using System.Collections.ObjectModel;
using DeploymentManager.Models;
using DeploymentManager.Services;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IConfigurationService _configurationService;
    private readonly IFolderPickerService _folderPicker;
    private readonly IMappingDialogService _mappingDialog;
    private readonly IDeploymentPlanner _deploymentPlanner;
    private readonly IDeploymentService _deploymentService;
    private readonly IDeploymentRollbackService _deploymentRollbackService;
    private readonly IDeploymentPreviewService _deploymentPreview;
    private readonly IRollbackConfirmationService _rollbackConfirmation;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _deploymentCancellation;
    private DeploymentOptions _deploymentOptions = new();
    private LastDeploymentRecord _lastDeployment = new();
    private string _backupRoot = string.Empty;
    private DeploymentMapping? _selectedMapping;
    private string _statusMessage = "Loading configuration...";
    private string _progressMessage = "Ready";
    private double _overallProgress;
    private bool _isDeploying;

    public MainViewModel(
        IConfigurationService configurationService,
        IFolderPickerService folderPicker,
        IMappingDialogService mappingDialog,
        IDeploymentPlanner deploymentPlanner,
        IDeploymentService deploymentService,
        IDeploymentRollbackService deploymentRollbackService,
        IDeploymentPreviewService deploymentPreview,
        IRollbackConfirmationService rollbackConfirmation,
        IUserNotificationService notifications,
        PackageBuilderViewModel packageBuilder,
        ILogger<MainViewModel> logger)
    {
        _configurationService = configurationService;
        _folderPicker = folderPicker;
        _mappingDialog = mappingDialog;
        _deploymentPlanner = deploymentPlanner;
        _deploymentService = deploymentService;
        _deploymentRollbackService = deploymentRollbackService;
        _deploymentPreview = deploymentPreview;
        _rollbackConfirmation = rollbackConfirmation;
        _notifications = notifications;
        PackageBuilder = packageBuilder;
        PackageBuilder.SaveSettingsAsync = SaveAsync;
        _logger = logger;
        AddCommand = new RelayCommand(AddMapping);
        EditCommand = new RelayCommand(
            parameter => EditMapping(parameter as DeploymentMapping ?? SelectedMapping),
            parameter => parameter is DeploymentMapping || SelectedMapping is not null);
        RemoveCommand = new RelayCommand(
            parameter => RemoveMapping(parameter as DeploymentMapping ?? SelectedMapping),
            parameter => parameter is DeploymentMapping || SelectedMapping is not null);
        SaveCommand = new AsyncRelayCommand(SaveWithConfirmationAsync);
        BrowseBackupRootCommand = new RelayCommand(BrowseBackupRoot);
        SelectAllCommand = new RelayCommand(() => SetDeploymentSelection(true));
        SelectNoneCommand = new RelayCommand(() => SetDeploymentSelection(false));
        PreviewDeploymentCommand = new AsyncRelayCommand(PreviewDeploymentAsync, () => !IsDeploying);
        CancelDeploymentCommand = new RelayCommand(CancelDeployment, () => IsDeploying);
        RollbackAllCommand = new AsyncRelayCommand(RollbackAllAsync, CanRollbackAny);
        RollbackApplicationCommand = new AsyncRelayCommand(
            parameter => RollbackApplicationAsync(parameter as string ?? string.Empty),
            parameter => parameter is string applicationName && CanRollbackApplication(applicationName));
    }

    public ObservableCollection<DeploymentMapping> Mappings { get; } = [];
    public PackageBuilderViewModel PackageBuilder { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand BrowseBackupRootCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public AsyncRelayCommand PreviewDeploymentCommand { get; }
    public RelayCommand CancelDeploymentCommand { get; }
    public AsyncRelayCommand RollbackAllCommand { get; }
    public AsyncRelayCommand RollbackApplicationCommand { get; }
    public ObservableCollection<string> DeploymentLog { get; } = [];
    public ObservableCollection<DeployedFileRecord> LastDeploymentFiles { get; } = [];
    public string BackupRoot { get => _backupRoot; set => SetProperty(ref _backupRoot, value); }
    public string LastDeployedText { get; private set; } = "Never";
    public string LastDeploymentStatusText { get; private set; } = "No deployment";
    public string LastDeploymentFilesSummary { get; private set; } = "No deployment has been recorded yet.";
    public string ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public double OverallProgress { get => _overallProgress; private set => SetProperty(ref _overallProgress, value); }
    public bool IsDeploying
    {
        get => _isDeploying;
        private set
        {
            if (SetProperty(ref _isDeploying, value))
            {
                PreviewDeploymentCommand.NotifyCanExecuteChanged();
                CancelDeploymentCommand.NotifyCanExecuteChanged();
                RollbackAllCommand.NotifyCanExecuteChanged();
                RollbackApplicationCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public DeploymentMapping? SelectedMapping
    {
        get => _selectedMapping;
        set
        {
            if (SetProperty(ref _selectedMapping, value))
            {
                EditCommand.NotifyCanExecuteChanged();
                RemoveCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public async Task InitializeAsync()
    {
        try
        {
            var configuration = await _configurationService.LoadAsync();
            BackupRoot = configuration.BackupRoot;
            _deploymentOptions = configuration.DeploymentOptions ?? new DeploymentOptions();
            _lastDeployment = configuration.LastDeployment ?? new LastDeploymentRecord();
            PackageBuilder.Load(configuration.PackageBuilder ?? new PackageBuilderSettings());
            LastDeploymentFiles.Clear();
            foreach (var file in _lastDeployment.Files)
            {
                LastDeploymentFiles.Add(file);
            }

            UpdateLastDeploymentDisplay();
            Mappings.Clear();
            foreach (var mapping in configuration.Mappings)
            {
                mapping.SelectedForDeployment = IsConfigured(mapping);
                Mappings.Add(mapping);
            }

            StatusMessage = $"{Mappings.Count} mappings loaded · {_configurationService.SettingsPath}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load deployment configuration");
            StatusMessage = $"Configuration could not be loaded: {exception.Message}";
            _notifications.ShowError("Configuration could not be loaded", exception.Message);
        }
    }

    private void AddMapping()
    {
        var mapping = _mappingDialog.EditMapping(null);
        if (mapping is null)
        {
            return;
        }

        mapping.SelectedForDeployment = mapping.Enabled;
        Mappings.Add(mapping);
        SelectedMapping = mapping;
        _ = SaveAsync();
    }

    private void EditMapping(DeploymentMapping? mapping)
    {
        mapping ??= SelectedMapping;
        if (mapping is null)
        {
            return;
        }

        var selection = mapping.SelectedForDeployment;
        var updated = _mappingDialog.EditMapping(mapping);
        if (updated is null)
        {
            return;
        }

        updated.SelectedForDeployment = selection;
        var index = Mappings.IndexOf(mapping);
        Mappings[index] = updated;
        SelectedMapping = updated;
        _ = SaveAsync();
    }

    private void RemoveMapping(DeploymentMapping? mapping)
    {
        mapping ??= SelectedMapping;
        if (mapping is null)
        {
            return;
        }

        if (!_rollbackConfirmation.Confirm(
                $"Remove the project '{mapping.Name}'?{Environment.NewLine}{Environment.NewLine}This removes its deployment mapping but does not delete any files.",
                "Remove project"))
        {
            return;
        }

        Mappings.Remove(mapping);
        if (SelectedMapping == mapping)
        {
            SelectedMapping = null;
        }

        _ = SaveAsync();
    }

    private void BrowseBackupRoot()
    {
        var selected = _folderPicker.PickFolder(BackupRoot, "Select global backup folder");
        if (selected is not null)
        {
            BackupRoot = selected;
            _ = SaveAsync();
        }
    }

    private void SetDeploymentSelection(bool selected)
    {
        foreach (var mapping in Mappings)
        {
            mapping.SelectedForDeployment = selected && IsConfigured(mapping);
        }
    }

    private async Task PreviewDeploymentAsync()
    {
        var selectedMappings = Mappings.Where(mapping => mapping.Enabled && mapping.SelectedForDeployment).ToArray();
        if (selectedMappings.Length == 0)
        {
            StatusMessage = "Select at least one enabled mapping with source and destination folders.";
            _notifications.ShowWarning("No projects selected", StatusMessage);
            return;
        }

        try
        {
            var plans = new List<DeploymentPlan>(selectedMappings.Length);
            foreach (var mapping in selectedMappings)
            {
                var plan = await Task.Run(
                    () => _deploymentPlanner.CreatePlanAsync(mapping, _deploymentOptions.HashComparisonEnabled),
                    CancellationToken.None);
                plans.Add(plan);
            }

            if (_deploymentPreview.Confirm(plans))
            {
                await DeployAsync(plans);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not create a deployment preview");
            StatusMessage = $"Preview failed: {exception.Message}";
            AddLog($"Preview failed: {exception.Message}");
            _notifications.ShowError("Deployment preview failed", exception.Message);
        }
    }

    private async Task DeployAsync(IReadOnlyList<DeploymentPlan> plans)
    {
        IsDeploying = true;
        OverallProgress = 0;
        ProgressMessage = "Preparing deployment...";
        _deploymentCancellation = new CancellationTokenSource();
        AddLog($"Deployment started for {plans.Count} application(s).");

        try
        {
            var progress = new Progress<DeploymentProgress>(UpdateProgress);
            var results = await _deploymentService.DeployAsync(
                plans,
                BackupRoot.Trim(),
                _deploymentOptions,
                progress,
                _deploymentCancellation.Token);

            OverallProgress = 100;
            ProgressMessage = "Deployment completed";
            foreach (var result in results)
            {
                AddLog($"{result.ApplicationName}: {result.NewFilesCopied} new, {result.ChangedFilesCopied} modified, {result.UnchangedFilesSkipped} unchanged.");
            }

            RecordLastDeployment(results, plans, "Success");
            await SaveAsync();
            if (!StatusMessage.StartsWith("Save failed:", StringComparison.Ordinal))
            {
                StatusMessage = "Deployment completed successfully.";
                _notifications.ShowInformation("Deployment completed", $"Deployment completed for {results.Count} application(s).");
            }
        }
        catch (DeploymentCancelledException exception)
        {
            await RecordPartialDeploymentAsync(exception.Results, plans, "Cancelled");
            ProgressMessage = "Deployment cancelled; completed files and backups were retained.";
            StatusMessage = ProgressMessage;
            AddLog(StatusMessage);
        }
        catch (IisApplicationPoolRestartException exception)
        {
            await RecordPartialDeploymentAsync(exception.Results, plans, "Failed");
            _logger.LogCritical(exception, "IIS pool restart failed after deployment");
            ProgressMessage = "Deployment completed, but IIS did not restart";
            StatusMessage = exception.Message;
            AddLog(exception.Message);
            _notifications.ShowError("IIS restart failed", exception.Message);
        }
        catch (DeploymentFailedException exception)
        {
            await RecordPartialDeploymentAsync(exception.Results, plans, "Failed");
            _logger.LogError(exception, "Deployment failed");
            ProgressMessage = "Deployment failed; completed files are available for rollback";
            StatusMessage = exception.Message;
            AddLog($"Deployment failed: {exception.Message}");
            _notifications.ShowError("Deployment failed", exception.Message);
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Deployment cancelled before files changed.";
            StatusMessage = ProgressMessage;
            AddLog(StatusMessage);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Deployment failed");
            ProgressMessage = "Deployment failed";
            StatusMessage = exception.Message;
            AddLog($"Deployment failed: {exception.Message}");
            _notifications.ShowError("Deployment failed", exception.Message);
        }
        finally
        {
            _deploymentCancellation.Dispose();
            _deploymentCancellation = null;
            IsDeploying = false;
        }
    }

    private void UpdateProgress(DeploymentProgress progress)
    {
        OverallProgress = progress.Percentage;
        ProgressMessage = $"{progress.ApplicationName} · {progress.CompletedFiles}/{progress.TotalFiles} · {progress.CurrentFile}";
        if (progress.Message is "Copying" or "Merging configuration")
        {
            AddLog($"{progress.ApplicationName}: {progress.Message.ToLowerInvariant()} {progress.CurrentFile}");
        }
        else if (progress.Message.Contains("IIS application pool", StringComparison.OrdinalIgnoreCase))
        {
            AddLog($"{progress.ApplicationName}: {progress.Message}");
        }
    }

    private void CancelDeployment()
    {
        _deploymentCancellation?.Cancel();
    }

    private bool CanRollbackAny() =>
        !IsDeploying && _lastDeployment.Applications.Any(HasRollbackWork);

    private bool CanRollbackApplication(string applicationName) =>
        !IsDeploying
        && _lastDeployment.Applications.Any(application =>
            application.ApplicationName.Equals(applicationName, StringComparison.OrdinalIgnoreCase)
            && HasRollbackWork(application));

    private Task RollbackAllAsync() => RollbackAsync(null);

    private Task RollbackApplicationAsync(string applicationName) => RollbackAsync(applicationName);

    private async Task RollbackAsync(string? applicationName)
    {
        if (!CanRollbackAny() || (applicationName is not null && !CanRollbackApplication(applicationName)))
        {
            return;
        }

        var targets = _lastDeployment.Applications
            .Where(application => HasRollbackWork(application)
                && (applicationName is null || application.ApplicationName.Equals(applicationName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var targetNames = string.Join(Environment.NewLine, targets.Select(application => $"• {application.ApplicationName}"));
        var scope = applicationName is null ? "the latest deployment" : $"the latest deployment for {applicationName}";
        var fileCount = targets.Sum(application => application.BackedUpFiles.Count + application.CreatedFiles.Count);
        if (!_rollbackConfirmation.Confirm(
            $"Roll back {scope}?{Environment.NewLine}{Environment.NewLine}{targetNames}{Environment.NewLine}{Environment.NewLine}{fileCount} file(s) will be restored or removed.",
            "Confirm rollback"))
        {
            return;
        }

        IsDeploying = true;
        OverallProgress = 0;
        ProgressMessage = "Preparing rollback...";
        _deploymentCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DeploymentProgress>(UpdateProgress);
            var results = await _deploymentRollbackService.RollbackAsync(
                _lastDeployment,
                applicationName,
                progress,
                _deploymentCancellation.Token);

            UpdateLastDeploymentDisplay();
            await SaveAsync();
            var summary = string.Join("; ", results.Select(result =>
                $"{result.ApplicationName}: restored {result.RestoredFiles}, removed {result.RemovedFiles}"));
            ProgressMessage = "Rollback completed";
            StatusMessage = $"Rollback completed. {summary}";
            AddLog(StatusMessage);
            OverallProgress = 100;
            _notifications.ShowInformation("Rollback completed", summary);
        }
        catch (RollbackFailedException exception)
        {
            UpdateLastDeploymentDisplay();
            await SaveAsync();
            _logger.LogError(exception, "Rollback failed for {ApplicationName}", exception.ApplicationName);
            ProgressMessage = "Rollback failed";
            StatusMessage = exception.Message;
            AddLog(StatusMessage);
            _notifications.ShowError("Rollback failed", exception.Message);
        }
        catch (OperationCanceledException)
        {
            UpdateLastDeploymentDisplay();
            await SaveAsync();
            ProgressMessage = "Rollback cancelled; completed changes are recorded.";
            StatusMessage = ProgressMessage;
            AddLog(StatusMessage);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Rollback could not start");
            ProgressMessage = "Rollback could not start";
            StatusMessage = exception.Message;
            AddLog(StatusMessage);
            _notifications.ShowError("Rollback could not start", exception.Message);
        }
        finally
        {
            _deploymentCancellation.Dispose();
            _deploymentCancellation = null;
            IsDeploying = false;
        }
    }

    private void RecordLastDeployment(
        IEnumerable<DeploymentResult> deploymentResults,
        IReadOnlyList<DeploymentPlan> plans,
        string status)
    {
        var results = deploymentResults.ToArray();
        var files = results.SelectMany(result => result.DeployedFiles.Select(path => new DeployedFileRecord
        {
            ApplicationName = result.ApplicationName,
            RelativePath = path
        })).ToList();

        var applications = results.Select(result =>
        {
            var mapping = plans.First(plan => plan.Mapping.Name.Equals(result.ApplicationName, StringComparison.OrdinalIgnoreCase)).Mapping;
            return new DeploymentApplicationRecord
            {
                ApplicationName = result.ApplicationName,
                DestinationFolder = mapping.DestinationFolder,
                BackupDirectory = result.BackupPath,
                ManageIisAppPool = mapping.ManageIisAppPool,
                IisServer = mapping.IisServer,
                IisAppPoolName = mapping.IisAppPoolName,
                Status = result.Status,
                CreatedFiles = [.. result.CreatedFiles],
                ChangedFiles = [.. result.ChangedFiles],
                BackedUpFiles = [.. result.BackedUpFiles]
            };
        }).ToList();

        _lastDeployment = new LastDeploymentRecord
        {
            DeploymentId = Guid.NewGuid().ToString("N"),
            DeployedAtUtc = DateTime.UtcNow,
            Status = status,
            Files = files,
            Applications = applications
        };

        LastDeploymentFiles.Clear();
        foreach (var file in files)
        {
            LastDeploymentFiles.Add(file);
        }

        UpdateLastDeploymentDisplay();
    }

    private async Task RecordPartialDeploymentAsync(
        IReadOnlyList<DeploymentResult> results,
        IReadOnlyList<DeploymentPlan> plans,
        string status)
    {
        if (results.Count == 0)
        {
            return;
        }

        RecordLastDeployment(results, plans, status);
        await SaveAsync();
    }

    private void UpdateLastDeploymentDisplay()
    {
        LastDeployedText = _lastDeployment.DeployedAtUtc?.ToLocalTime().ToString("dd MMM yyyy, HH:mm") ?? "Never";
        LastDeploymentStatusText = _lastDeployment.Status;
        LastDeploymentFilesSummary = _lastDeployment.DeployedAtUtc is null
            ? "No deployment has been recorded yet."
            : LastDeploymentFiles.Count == 0
                ? $"No files changed in the latest deployment ({_lastDeployment.Status})."
                : $"{LastDeploymentFiles.Count} file(s) across {LastDeploymentFiles.Select(file => file.ApplicationName).Distinct(StringComparer.OrdinalIgnoreCase).Count()} application(s) · {_lastDeployment.Status}.";
        OnPropertyChanged(nameof(LastDeployedText));
        OnPropertyChanged(nameof(LastDeploymentStatusText));
        OnPropertyChanged(nameof(LastDeploymentFilesSummary));
    }

    private void AddLog(string message)
    {
        DeploymentLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (DeploymentLog.Count > 1000)
        {
            DeploymentLog.RemoveAt(DeploymentLog.Count - 1);
        }
    }

    private static bool IsConfigured(DeploymentMapping mapping) =>
        mapping.Enabled
        && !string.IsNullOrWhiteSpace(mapping.SourceFolder)
        && !string.IsNullOrWhiteSpace(mapping.DestinationFolder);

    private static bool HasRollbackWork(DeploymentApplicationRecord application) =>
        application.RolledBackAtUtc is null
        && (application.CreatedFiles.Count > 0 || application.ChangedFiles.Count > 0);

    private async Task SaveAsync()
    {
        try
        {
            await _configurationService.SaveAsync(new DeploymentConfiguration
            {
                BackupRoot = BackupRoot.Trim(),
                Mappings = [.. Mappings.Select(mapping => mapping.Copy())],
                DeploymentOptions = _deploymentOptions,
                LastDeployment = _lastDeployment,
                PackageBuilder = PackageBuilder.ToSettings()
            });

            StatusMessage = $"Saved · {_configurationService.SettingsPath}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to save deployment configuration");
            StatusMessage = $"Save failed: {exception.Message}";
            _notifications.ShowError("Settings could not be saved", exception.Message);
        }
    }

    private async Task SaveWithConfirmationAsync()
    {
        await SaveAsync();
        if (!StatusMessage.StartsWith("Save failed:", StringComparison.Ordinal))
        {
            _notifications.ShowInformation("Settings saved", "Your deployment settings have been saved.");
        }
    }
}