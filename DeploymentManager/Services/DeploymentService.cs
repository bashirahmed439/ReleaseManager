using System.IO;
using System.Security.Cryptography;
using DeploymentManager.Models;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.Services;

public sealed class DeploymentService(
    IDeploymentPlanner planner,
    IIniConfigurationMerger iniMerger,
    IIisApplicationPoolService iisApplicationPoolService,
    IJavaScriptScaffoldingService javascriptScaffolding,
    ILogger<DeploymentService> logger) : IDeploymentService
{
    public async Task<IReadOnlyList<DeploymentResult>> DeployAsync(
        IReadOnlyList<DeploymentPlan> plans,
        string backupRoot,
        DeploymentOptions options,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var activePlans = new List<DeploymentPlan>(plans.Count);
        foreach (var confirmedPlan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refreshedPlan = await planner.CreatePlanAsync(confirmedPlan.Mapping, options.HashComparisonEnabled, cancellationToken);
            if (!HasSamePlan(confirmedPlan, refreshedPlan))
            {
                throw new DeploymentPlanStaleException(confirmedPlan.Mapping.Name);
            }

            activePlans.Add(refreshedPlan);
        }

        var results = new List<DeploymentResult>();
        var allFilesCount = activePlans.Sum(plan => plan.FilesToCopy);
        var completedFiles = 0;
        var backupSessionRoot = string.Empty;

        foreach (var plan in activePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(plan.Mapping.SourceFolder))
            {
                throw new DirectoryNotFoundException($"Source folder for '{plan.Mapping.Name}' no longer exists: {plan.Mapping.SourceFolder}");
            }

            ValidateBackup(plan, backupRoot);
            await EnsureWritableDirectoryAsync(plan.Mapping.DestinationFolder, cancellationToken);

            if (plan.BackupFileCount > 0)
            {
                var fullBackupRoot = Path.GetFullPath(backupRoot);
                if (DeploymentPlanner.PathsOverlap(fullBackupRoot, Path.GetFullPath(plan.Mapping.SourceFolder))
                    || DeploymentPlanner.PathsOverlap(fullBackupRoot, Path.GetFullPath(plan.Mapping.DestinationFolder)))
                {
                    throw new InvalidOperationException($"Backup root must not overlap the source or destination for '{plan.Mapping.Name}'.");
                }

                await EnsureWritableDirectoryAsync(fullBackupRoot, cancellationToken);
            }
        }

        foreach (var plan in activePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var backupPath = string.Empty;
            var result = new DeploymentResult
            {
                ApplicationName = plan.Mapping.Name,
                Status = "Running",
                BackupPath = string.Empty,
                UnchangedFilesSkipped = plan.UnchangedFiles.Count
            };
            results.Add(result);

            var shouldRestartIisAppPool = false;
            if (plan.Mapping.ManageIisAppPool && plan.FilesToCopy > 0)
            {
                progress?.Report(new DeploymentProgress(plan.Mapping.Name, string.Empty, completedFiles, allFilesCount, "Stopping IIS application pool"));
                shouldRestartIisAppPool = await iisApplicationPoolService.StopIfRunningAsync(plan.Mapping, CancellationToken.None);
                progress?.Report(new DeploymentProgress(
                    plan.Mapping.Name,
                    string.Empty,
                    completedFiles,
                    allFilesCount,
                    shouldRestartIisAppPool ? "IIS application pool stopped" : "IIS application pool was already stopped"));
            }

            try
            {
                foreach (var item in plan.NewFiles.Concat(plan.ChangedFiles))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isExistingIni = IsConfigurationIni(item.SourcePath) && File.Exists(item.DestinationPath);
                    progress?.Report(new DeploymentProgress(
                        plan.Mapping.Name,
                        item.RelativePath,
                        completedFiles,
                        allFilesCount,
                        isExistingIni ? "Merging configuration" : "Copying"));
                    Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
                    var outcome = await CopyDeploymentFileAsync(
                        item,
                        plan.Mapping,
                        backupRoot,
                        backupSessionRoot,
                        options,
                        cancellationToken,
                        path => backupPath = path);
                    backupSessionRoot = outcome.BackupSessionRoot;
                    if (!string.IsNullOrEmpty(outcome.BackupPath))
                    {
                        backupPath = outcome.BackupPath;
                    }

                    if (!outcome.Copied)
                    {
                        result.UnchangedFilesSkipped++;
                        progress?.Report(new DeploymentProgress(plan.Mapping.Name, item.RelativePath, ++completedFiles, allFilesCount, "Skipped unchanged"));
                        continue;
                    }

                    if (outcome.WasNew)
                    {
                        result.NewFilesCopied++;
                        result.CreatedFiles.Add(item.RelativePath);
                    }
                    else
                    {
                        result.ChangedFilesCopied++;
                        result.ChangedFiles.Add(item.RelativePath);
                    }

                    if (outcome.WasBackedUp)
                    {
                        result.BackedUpFiles.Add(item.RelativePath);
                    }

                    result.DeployedFiles.Add(item.RelativePath);
                    completedFiles++;
                    progress?.Report(new DeploymentProgress(plan.Mapping.Name, item.RelativePath, completedFiles, allFilesCount, "Copied"));
                }

                result.BackupPath = backupPath;
                result.Status = "Success";
                logger.LogInformation("Deployment completed for {ApplicationName}: {NewCount} new, {ChangedCount} changed, {UnchangedCount} unchanged", plan.Mapping.Name, result.NewFilesCopied, result.ChangedFilesCopied, result.UnchangedFilesSkipped);
            }
            catch (OperationCanceledException)
            {
                result.Status = "Cancelled";
                result.BackupPath = backupPath;
                logger.LogWarning("Deployment cancelled for {ApplicationName}; backup retained at {BackupPath}", plan.Mapping.Name, backupPath);
                throw new DeploymentCancelledException(plan.Mapping.Name, results.ToArray());
            }
            catch (Exception exception)
            {
                result.Status = "Failed";
                result.ErrorMessage = exception.Message;
                result.BackupPath = backupPath;
                logger.LogError(exception, "Deployment failed for {ApplicationName}", plan.Mapping.Name);
                throw new DeploymentFailedException(plan.Mapping.Name, exception, results.ToArray());
            }
            finally
            {
                if (shouldRestartIisAppPool)
                {
                    progress?.Report(new DeploymentProgress(plan.Mapping.Name, string.Empty, completedFiles, allFilesCount, "Starting IIS application pool"));
                    try
                    {
                        await iisApplicationPoolService.StartAsync(plan.Mapping, CancellationToken.None);
                    }
                    catch (Exception restartException)
                    {
                        result.Status = "Failed";
                        result.BackupPath = backupPath;
                        result.ErrorMessage = $"IIS application pool '{plan.Mapping.IisAppPoolName}' could not be restarted. Start it manually. {restartException.Message}";
                        logger.LogCritical(restartException, "IIS application pool {ApplicationPool} for {ApplicationName} could not be restarted", plan.Mapping.IisAppPoolName, plan.Mapping.Name);
                        throw new IisApplicationPoolRestartException(plan.Mapping.Name, plan.Mapping.IisAppPoolName, restartException, results.ToArray());
                    }
                }
            }
        }

        return results;
    }

    private async Task<CopyOutcome> CopyDeploymentFileAsync(
        FileDeploymentItem item,
        DeploymentMapping mapping,
        string backupRoot,
        string backupSessionRoot,
        DeploymentOptions options,
        CancellationToken cancellationToken,
        Action<string> backupPathUpdated)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsConfigurationIni(item.SourcePath) && File.Exists(item.DestinationPath))
            {
                try
                {
                    var mergeOutcome = await MergeConfigurationIniAsync(
                        item,
                        mapping,
                        backupRoot,
                        backupSessionRoot,
                        cancellationToken,
                        backupPathUpdated);
                    return mergeOutcome;
                }
                catch (IOException exception) when (attempt <= options.RetryAttempts)
                {
                    logger.LogWarning(exception, "Configuration merge attempt {Attempt}/{MaxAttempts} failed for {Path}", attempt, options.RetryAttempts + 1, item.DestinationPath);
                    await Task.Delay(options.RetryDelayMilliseconds, cancellationToken);
                    continue;
                }
                catch (UnauthorizedAccessException exception) when (attempt <= options.RetryAttempts)
                {
                    logger.LogWarning(exception, "Configuration merge attempt {Attempt}/{MaxAttempts} failed for {Path}", attempt, options.RetryAttempts + 1, item.DestinationPath);
                    await Task.Delay(options.RetryDelayMilliseconds, cancellationToken);
                    continue;
                }
            }

            var stagingPath = Path.Combine(
                Path.GetDirectoryName(item.DestinationPath)!,
                $".deployment-manager-{Guid.NewGuid():N}.tmp");
            try
            {
                if (mapping.JavaScriptScaffoldingEnabled && Path.GetExtension(item.SourcePath).Equals(".js", StringComparison.OrdinalIgnoreCase))
                {
                    await javascriptScaffolding.ProcessAsync(item.SourcePath, stagingPath, mapping, cancellationToken);
                }
                else
                {
                    await CopyFileAsync(item.SourcePath, stagingPath, cancellationToken);
                }

                File.SetLastWriteTimeUtc(stagingPath, File.GetLastWriteTimeUtc(item.SourcePath));
                if (options.VerifyFilesAfterCopy && !await FilesMatchAsync(item.SourcePath, stagingPath, cancellationToken))
                {
                    throw new IOException($"Verification failed for staged file '{item.RelativePath}'.");
                }

                var destinationExists = File.Exists(item.DestinationPath);
                if (!await IsDifferentAsync(item.SourcePath, item.DestinationPath, destinationExists, options.HashComparisonEnabled, cancellationToken))
                {
                    return new CopyOutcome(false, false, false, string.Empty, backupSessionRoot);
                }

                var applicationBackupFolder = string.Empty;
                if (destinationExists
                    && mapping.BackupEnabled
                    && !BackupPathRules.IsExcluded(item.RelativePath, mapping.BackupExclusions))
                {
                    if (string.IsNullOrWhiteSpace(backupRoot))
                    {
                        throw new InvalidOperationException($"Backup is enabled for '{mapping.Name}', but no global backup root is configured.");
                    }

                    if (backupSessionRoot.Length == 0)
                    {
                        backupSessionRoot = CreateBackupSessionRoot(backupRoot);
                    }

                    applicationBackupFolder = Path.Combine(backupSessionRoot, SanitizeName(mapping.Name));
                    backupPathUpdated(applicationBackupFolder);
                    var backupFile = Path.Combine(applicationBackupFolder, item.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    await CopyFileWithRetryAsync(item.DestinationPath, backupFile, options, cancellationToken);
                    File.SetLastWriteTimeUtc(backupFile, File.GetLastWriteTimeUtc(item.DestinationPath));
                    logger.LogInformation("Backed up {FilePath} for {ApplicationName} to {BackupPath}", item.RelativePath, mapping.Name, backupFile);
                }

                File.Move(stagingPath, item.DestinationPath, overwrite: destinationExists);
                return new CopyOutcome(true, !destinationExists, !string.IsNullOrEmpty(applicationBackupFolder), applicationBackupFolder, backupSessionRoot);
            }
            catch (IOException exception) when (attempt <= options.RetryAttempts)
            {
                logger.LogWarning(exception, "Copy attempt {Attempt}/{MaxAttempts} failed for {Path}", attempt, options.RetryAttempts + 1, item.DestinationPath);
                await Task.Delay(options.RetryDelayMilliseconds, cancellationToken);
            }
            catch (UnauthorizedAccessException exception) when (attempt <= options.RetryAttempts)
            {
                logger.LogWarning(exception, "Copy attempt {Attempt}/{MaxAttempts} failed for {Path}", attempt, options.RetryAttempts + 1, item.DestinationPath);
                await Task.Delay(options.RetryDelayMilliseconds, cancellationToken);
            }
            finally
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
        }
    }

    private async Task<CopyOutcome> MergeConfigurationIniAsync(
        FileDeploymentItem item,
        DeploymentMapping mapping,
        string backupRoot,
        string backupSessionRoot,
        CancellationToken cancellationToken,
        Action<string> backupPathUpdated)
    {
        var applicationBackupFolder = string.Empty;
        var addedEntries = await iniMerger.AppendMissingEntriesAsync(
            item.SourcePath,
            item.DestinationPath,
            async originalBytes =>
            {
                if (!mapping.BackupEnabled || BackupPathRules.IsExcluded(item.RelativePath, mapping.BackupExclusions))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(backupRoot))
                {
                    throw new InvalidOperationException($"Backup is enabled for '{mapping.Name}', but no global backup root is configured.");
                }

                if (backupSessionRoot.Length == 0)
                {
                    backupSessionRoot = CreateBackupSessionRoot(backupRoot);
                }

                applicationBackupFolder = Path.Combine(backupSessionRoot, SanitizeName(mapping.Name));
                backupPathUpdated(applicationBackupFolder);
                var backupFile = Path.Combine(applicationBackupFolder, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                await File.WriteAllBytesAsync(backupFile, originalBytes, cancellationToken);
                File.SetLastWriteTimeUtc(backupFile, File.GetLastWriteTimeUtc(item.DestinationPath));
                logger.LogInformation("Backed up {RelativePath} before configuration merge for {ApplicationName} to {BackupPath}",
                    item.RelativePath, mapping.Name, backupFile);
            },
            cancellationToken);

        if (addedEntries == 0)
        {
            return new CopyOutcome(false, false, false, string.Empty, backupSessionRoot);
        }

        logger.LogInformation("Appended {EntryCount} missing configuration entries to {DestinationPath}", addedEntries, item.DestinationPath);
        return new CopyOutcome(true, false, !string.IsNullOrEmpty(applicationBackupFolder), applicationBackupFolder, backupSessionRoot);
    }

    private async Task CopyFileWithRetryAsync(string sourcePath, string destinationPath, DeploymentOptions options, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(() => CopyFileAsync(sourcePath, destinationPath, cancellationToken), destinationPath, options, cancellationToken);
    }

    private async Task ExecuteWithRetryAsync(Func<Task> operation, string path, DeploymentOptions options, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await operation();
                return;
            }
            catch (IOException exception) when (attempt <= options.RetryAttempts)
            {
                logger.LogWarning(exception, "Copy attempt {Attempt}/{MaxAttempts} failed for {Path}", attempt, options.RetryAttempts + 1, path);
                await Task.Delay(options.RetryDelayMilliseconds, cancellationToken);
            }
            catch (UnauthorizedAccessException exception) when (attempt <= options.RetryAttempts)
            {
                logger.LogWarning(exception, "Copy attempt {Attempt}/{MaxAttempts} failed for {Path}", attempt, options.RetryAttempts + 1, path);
                await Task.Delay(options.RetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task EnsureWritableDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directoryPath);
        var probePath = Path.Combine(directoryPath, $".deployment-manager-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
            await probe.WriteAsync(new byte[] { 0 }, cancellationToken);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static async Task<bool> FilesMatchAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);
        if (sourceInfo.Length != destinationInfo.Length)
        {
            return false;
        }

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var sourceHash = await SHA256.HashDataAsync(source, cancellationToken);
        var destinationHash = await SHA256.HashDataAsync(destination, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
    }

    private static async Task<bool> IsDifferentAsync(
        string sourcePath,
        string destinationPath,
        bool destinationExists,
        bool hashComparisonEnabled,
        CancellationToken cancellationToken)
    {
        if (!destinationExists)
        {
            return true;
        }

        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);
        if (sourceInfo.Length != destinationInfo.Length)
        {
            return true;
        }

        if (!hashComparisonEnabled)
        {
            return sourceInfo.LastWriteTimeUtc != destinationInfo.LastWriteTimeUtc;
        }

        return !await FilesMatchAsync(sourcePath, destinationPath, cancellationToken);
    }

    private static bool HasSamePlan(DeploymentPlan previewPlan, DeploymentPlan currentPlan) =>
        SamePaths(previewPlan.NewFiles, currentPlan.NewFiles)
        && SamePaths(previewPlan.ChangedFiles, currentPlan.ChangedFiles)
        && SamePaths(previewPlan.UnchangedFiles, currentPlan.UnchangedFiles);

    private static bool IsConfigurationIni(string path) =>
        Path.GetFileName(path).Equals("configuration.ini", StringComparison.OrdinalIgnoreCase);

    private static bool SamePaths(IEnumerable<FileDeploymentItem> first, IEnumerable<FileDeploymentItem> second) =>
        new HashSet<string>(first.Select(item => item.RelativePath), StringComparer.OrdinalIgnoreCase)
            .SetEquals(second.Select(item => item.RelativePath));

    private static void ValidateBackup(DeploymentPlan plan, string backupRoot)
    {
        if (!plan.Mapping.BackupEnabled)
        {
            return;
        }

        if (plan.ChangedFiles.Any(item => File.Exists(item.DestinationPath) && !BackupPathRules.IsExcluded(item.RelativePath, plan.Mapping.BackupExclusions))
            && string.IsNullOrWhiteSpace(backupRoot))
        {
            throw new InvalidOperationException($"Backup is enabled for '{plan.Mapping.Name}', but no global backup root is configured.");
        }
    }

    private static string SanitizeName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Application" : sanitized;
    }

    private static string CreateBackupSessionRoot(string backupRoot)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var sessionRoot = Path.Combine(Path.GetFullPath(backupRoot), timestamp);
        for (var suffix = 1; Directory.Exists(sessionRoot); suffix++)
        {
            sessionRoot = Path.Combine(Path.GetFullPath(backupRoot), $"{timestamp}_{suffix}");
        }

        Directory.CreateDirectory(sessionRoot);
        return sessionRoot;
    }

    private static void ValidateOptions(DeploymentOptions options)
    {
        if (options.RetryAttempts < 0 || options.RetryAttempts > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry attempts must be between 0 and 10.");
        }

        if (options.RetryDelayMilliseconds < 0 || options.RetryDelayMilliseconds > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delay must be between 0 and 60000 milliseconds.");
        }
    }

    private sealed record CopyOutcome(bool Copied, bool WasNew, bool WasBackedUp, string BackupPath, string BackupSessionRoot);
}

public sealed class DeploymentFailedException(string applicationName, Exception innerException, IReadOnlyList<DeploymentResult> results)
    : Exception($"Deployment failed for '{applicationName}'. {innerException.Message}", innerException)
{
    public string ApplicationName { get; } = applicationName;
    public IReadOnlyList<DeploymentResult> Results { get; } = results;
}

public sealed class DeploymentCancelledException(string applicationName, IReadOnlyList<DeploymentResult> results)
    : OperationCanceledException($"Deployment was cancelled for '{applicationName}'.")
{
    public string ApplicationName { get; } = applicationName;
    public IReadOnlyList<DeploymentResult> Results { get; } = results;
}

public sealed class DeploymentPlanStaleException(string applicationName)
    : Exception($"The files for '{applicationName}' changed after preview. Preview the deployment again before deploying.")
{
    public string ApplicationName { get; } = applicationName;
}

public sealed class IisApplicationPoolRestartException(
    string applicationName,
    string applicationPoolName,
    Exception innerException,
    IReadOnlyList<DeploymentResult> results)
    : Exception($"IIS application pool '{applicationPoolName}' for '{applicationName}' could not be restarted. Start it manually. {innerException.Message}", innerException)
{
    public string ApplicationName { get; } = applicationName;
    public string ApplicationPoolName { get; } = applicationPoolName;
    public IReadOnlyList<DeploymentResult> Results { get; } = results;
}