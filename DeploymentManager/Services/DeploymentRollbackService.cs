using System.IO;
using DeploymentManager.Models;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.Services;

public sealed class DeploymentRollbackService(
    IIisApplicationPoolService iisApplicationPoolService,
    ILogger<DeploymentRollbackService> logger) : IDeploymentRollbackService
{
    public async Task<IReadOnlyList<RollbackApplicationResult>> RollbackAsync(
        LastDeploymentRecord deployment,
        string? applicationName = null,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var applications = deployment.Applications
            .Where(application => application.RolledBackAtUtc is null
                && HasRollbackWork(application)
                && (string.IsNullOrWhiteSpace(applicationName)
                    || application.ApplicationName.Equals(applicationName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (applications.Length == 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(applicationName)
                ? "There are no applications left to roll back in the latest deployment."
                : $"'{applicationName}' has no pending changes to roll back in the latest deployment.");
        }

        foreach (var application in applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateApplication(application);
        }

        var results = new List<RollbackApplicationResult>(applications.Length);
        var totalFiles = applications.Sum(application => application.BackedUpFiles.Count + application.CreatedFiles.Count);
        var completedFiles = 0;

        foreach (var application in applications)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                deployment.Status = deployment.Applications.Where(HasRollbackWork).All(item => item.RolledBackAtUtc is not null)
                    ? "Rolled back"
                    : "Partially rolled back";
                throw new OperationCanceledException(cancellationToken);
            }

            var result = new RollbackApplicationResult { ApplicationName = application.ApplicationName };
            results.Add(result);
            var shouldRestartIis = false;
            try
            {
                var filesToChange = application.BackedUpFiles.Count + application.CreatedFiles.Count;
                if (application.ManageIisAppPool && filesToChange > 0)
                {
                    progress?.Report(new DeploymentProgress(application.ApplicationName, string.Empty, completedFiles, totalFiles, "Stopping IIS application pool for rollback"));
                    shouldRestartIis = await iisApplicationPoolService.StopIfRunningAsync(ToMapping(application), CancellationToken.None);
                }

                foreach (var relativePath in application.BackedUpFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var backupPath = ResolveInside(application.BackupDirectory, relativePath);
                    var destinationPath = ResolveInside(application.DestinationFolder, relativePath);
                    progress?.Report(new DeploymentProgress(application.ApplicationName, relativePath, completedFiles, totalFiles, "Restoring original file"));
                    await RestoreFileAsync(backupPath, destinationPath, cancellationToken);
                    result.RestoredFiles++;
                    completedFiles++;
                }

                foreach (var relativePath in application.CreatedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destinationPath = ResolveInside(application.DestinationFolder, relativePath);
                    progress?.Report(new DeploymentProgress(application.ApplicationName, relativePath, completedFiles, totalFiles, "Removing deployment-created file"));
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                        result.RemovedFiles++;
                    }

                    completedFiles++;
                }

                result.Status = "Rolled back";
                application.Status = "Rolled back";
                application.RolledBackAtUtc = DateTime.UtcNow;
                logger.LogInformation("Rolled back {ApplicationName}: restored {RestoredCount}, removed {RemovedCount}",
                    application.ApplicationName, result.RestoredFiles, result.RemovedFiles);
            }
            catch (OperationCanceledException)
            {
                result.Status = "Cancelled";
                application.Status = "Rollback cancelled";
                deployment.Status = "Rollback partially complete";
                throw;
            }
            catch (Exception exception)
            {
                result.Status = "Failed";
                application.Status = "Rollback failed";
                deployment.Status = "Rollback failed";
                logger.LogError(exception, "Rollback failed for {ApplicationName}", application.ApplicationName);
                throw new RollbackFailedException(application.ApplicationName, results.ToArray(), exception);
            }
            finally
            {
                if (shouldRestartIis)
                {
                    progress?.Report(new DeploymentProgress(application.ApplicationName, string.Empty, completedFiles, totalFiles, "Restarting IIS application pool after rollback"));
                    try
                    {
                        await iisApplicationPoolService.StartAsync(ToMapping(application), CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        result.Status = "IIS restart failed";
                        application.Status = "IIS restart failed";
                        deployment.Status = "Rollback failed; IIS restart required";
                        logger.LogCritical(exception, "Could not restart IIS application pool {ApplicationPool} after rollback for {ApplicationName}",
                            application.IisAppPoolName, application.ApplicationName);
                        throw new RollbackFailedException(application.ApplicationName, results.ToArray(), exception);
                    }
                }
            }
        }

        deployment.Status = deployment.Applications.Where(HasRollbackWork).All(application => application.RolledBackAtUtc is not null)
            ? "Rolled back"
            : "Partially rolled back";
        return results;
    }

    private static void ValidateApplication(DeploymentApplicationRecord application)
    {
        if (string.IsNullOrWhiteSpace(application.DestinationFolder) || !Directory.Exists(application.DestinationFolder))
        {
            throw new DirectoryNotFoundException($"Destination for '{application.ApplicationName}' does not exist: {application.DestinationFolder}");
        }

        var missingBackups = application.ChangedFiles
            .Except(application.BackedUpFiles, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingBackups.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cannot safely roll back '{application.ApplicationName}'. No original backup exists for: {string.Join(", ", missingBackups)}");
        }

        foreach (var relativePath in application.BackedUpFiles)
        {
            var backupPath = ResolveInside(application.BackupDirectory, relativePath);
            ResolveInside(application.DestinationFolder, relativePath);
            if (!File.Exists(backupPath))
            {
                throw new FileNotFoundException(
                    $"The original backup for '{application.ApplicationName}' is missing: {backupPath}", backupPath);
            }
        }

        foreach (var relativePath in application.CreatedFiles)
        {
            ResolveInside(application.DestinationFolder, relativePath);
        }
    }

    private static bool HasRollbackWork(DeploymentApplicationRecord application) =>
        application.CreatedFiles.Count > 0 || application.ChangedFiles.Count > 0;

    private static async Task RestoreFileAsync(string backupPath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var stagingPath = Path.Combine(Path.GetDirectoryName(destinationPath)!, $".deployment-manager-rollback-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.SetLastWriteTimeUtc(stagingPath, File.GetLastWriteTimeUtc(backupPath));
            File.Move(stagingPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Unsafe rollback path '{relativePath}'.");
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var requiredPrefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Rollback path '{relativePath}' escapes its configured directory.");
        }

        return fullPath;
    }

    private static DeploymentMapping ToMapping(DeploymentApplicationRecord application) => new()
    {
        Name = application.ApplicationName,
        DestinationFolder = application.DestinationFolder,
        ManageIisAppPool = application.ManageIisAppPool,
        IisServer = application.IisServer,
        IisAppPoolName = application.IisAppPoolName
    };
}

public sealed class RollbackFailedException(
    string applicationName,
    IReadOnlyList<RollbackApplicationResult> results,
    Exception innerException)
    : Exception($"Rollback failed for '{applicationName}'. {innerException.Message}", innerException)
{
    public string ApplicationName { get; } = applicationName;
    public IReadOnlyList<RollbackApplicationResult> Results { get; } = results;
}