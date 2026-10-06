using System.IO;
using System.Security.Cryptography;
using DeploymentManager.Models;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.Services;

public sealed class DeploymentPlanner(
    IIniConfigurationMerger iniMerger,
    ILogger<DeploymentPlanner> logger) : IDeploymentPlanner
{
    public async Task<DeploymentPlan> CreatePlanAsync(
        DeploymentMapping mapping,
        bool hashComparisonEnabled,
        CancellationToken cancellationToken = default)
    {
        ValidateMapping(mapping);
        var sourceRoot = Path.GetFullPath(mapping.SourceFolder);
        var destinationRoot = Path.GetFullPath(mapping.DestinationFolder);
        var plan = new DeploymentPlan { Mapping = mapping.Copy() };

        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                var destinationPath = Path.Combine(destinationRoot, relativePath);
                var item = new FileDeploymentItem(relativePath, sourcePath, destinationPath);

                if (!File.Exists(destinationPath))
                {
                    plan.NewFiles.Add(item);
                    continue;
                }

                if (IsConfigurationIni(sourcePath))
                {
                    var hasNewEntries = await iniMerger.CountMissingEntriesAsync(sourcePath, destinationPath, cancellationToken) > 0;
                    (hasNewEntries ? plan.ChangedFiles : plan.UnchangedFiles).Add(item);
                    continue;
                }

                var sourceInfo = new FileInfo(sourcePath);
                var destinationInfo = new FileInfo(destinationPath);
                var differs = sourceInfo.Length != destinationInfo.Length;
                if (!differs && hashComparisonEnabled)
                {
                    differs = !await FilesMatchByHashAsync(sourcePath, destinationPath, cancellationToken);
                }
                else if (!differs)
                {
                    differs = sourceInfo.LastWriteTimeUtc != destinationInfo.LastWriteTimeUtc;
                }

                (differs ? plan.ChangedFiles : plan.UnchangedFiles).Add(item);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to scan deployment source for {ApplicationName}", mapping.Name);
            throw new IOException($"Could not scan source folder '{mapping.SourceFolder}' for '{mapping.Name}'. {exception.Message}", exception);
        }

        plan.BackupFileCount = mapping.BackupEnabled
            ? plan.ChangedFiles.Count(item => File.Exists(item.DestinationPath)
                && !BackupPathRules.IsExcluded(item.RelativePath, mapping.BackupExclusions))
            : 0;

        logger.LogInformation(
            "Planned {ApplicationName}: {NewCount} new, {ChangedCount} changed, {UnchangedCount} unchanged",
            mapping.Name, plan.NewFiles.Count, plan.ChangedFiles.Count, plan.UnchangedFiles.Count);
        return plan;
    }

    private static void ValidateMapping(DeploymentMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.Name))
        {
            throw new InvalidOperationException("An application mapping must have a name.");
        }
        if (mapping.ManageIisAppPool && string.IsNullOrWhiteSpace(mapping.IisAppPoolName))
        {
            throw new InvalidOperationException($"IIS application pool name is required for '{mapping.Name}'.");
        }

        if (string.IsNullOrWhiteSpace(mapping.SourceFolder) || !Directory.Exists(mapping.SourceFolder))
        {
            throw new DirectoryNotFoundException($"Source folder for '{mapping.Name}' does not exist: {mapping.SourceFolder}");
        }

        if (string.IsNullOrWhiteSpace(mapping.DestinationFolder))
        {
            throw new InvalidOperationException($"Destination folder for '{mapping.Name}' is empty.");
        }

        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.SourceFolder));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.DestinationFolder));
        if (PathsOverlap(source, destination))
        {
            throw new InvalidOperationException($"Source and destination folders for '{mapping.Name}' must not contain one another.");
        }
    }

    private static bool IsConfigurationIni(string path) =>
        Path.GetFileName(path).Equals("configuration.ini", StringComparison.OrdinalIgnoreCase);

    internal static bool PathsOverlap(string firstPath, string secondPath)
    {
        var first = Path.TrimEndingDirectorySeparator(firstPath) + Path.DirectorySeparatorChar;
        var second = Path.TrimEndingDirectorySeparator(secondPath) + Path.DirectorySeparatorChar;
        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> FilesMatchByHashAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var sourceHash = await SHA256.HashDataAsync(source, cancellationToken);
        var destinationHash = await SHA256.HashDataAsync(destination, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
    }
}