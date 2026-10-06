using System.IO;
using System.Text.RegularExpressions;
using DeploymentManager.Models;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.Services;

public sealed class PackageBuilderService(
    ILogger<PackageBuilderService> logger) : IPackageBuilderService
{
    public async Task<PackageBuildResult> BuildAsync(
        PackageBuilderSettings settings,
        bool includeAllFiles,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.SolutionFolder) || !Directory.Exists(settings.SolutionFolder))
        {
            throw new DirectoryNotFoundException($"The solution folder does not exist: {settings.SolutionFolder}");
        }

        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
        {
            throw new InvalidOperationException("Choose a folder for the deployable packages.");
        }

        var solutionRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.SolutionFolder));
        var outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.OutputFolder));
        var exclusions = ExclusionRules.Parse(settings.ExcludePatterns);
        var projects = SolutionProjectLocator.FindProjects(solutionRoot);
        if (projects.Count == 0)
        {
            throw new InvalidOperationException($"No Visual Studio projects were found in '{solutionRoot}'.");
        }

        var result = new PackageBuildResult();
        var pending = new List<PendingProject>();
        var cutoffUtc = DateTime.UtcNow.AddHours(-1);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectResult = new PackageProjectResult { ProjectName = project.Name };
            result.Projects.Add(projectResult);

            var publishFolder = SolutionProjectLocator.FindPublishFolder(project);
            if (publishFolder is null)
            {
                projectResult.SkippedReason = "No publish output found. Publish the project first.";
                continue;
            }

            if (SolutionProjectLocator.IsInside(outputRoot, publishFolder))
            {
                throw new InvalidOperationException($"The package folder must not be inside the publish folder of '{project.Name}'.");
            }

            progress?.Report($"Scanning {project.Name}...");
            projectResult.PublishFolder = publishFolder;
            await ScanAsync(publishFolder, cutoffUtc, exclusions, projectResult, cancellationToken);
            if (projectResult.Files.Count > 0)
            {
                pending.Add(new PendingProject(projectResult, publishFolder));
            }
        }

        if (pending.Count > 0)
        {
            progress?.Report("Creating deployable folders...");
            result.PackageRoot = await CreatePackageAsync(outputRoot, pending, cancellationToken);
        }

        logger.LogInformation("Package build for {SolutionFolder}: {FileCount} file(s) published within the last hour in {PackageRoot}",
            solutionRoot, result.TotalFiles, result.PackageRoot ?? "(none)");
        return result;
    }

    private async Task<string> CreatePackageAsync(
        string outputRoot,
        IReadOnlyList<PendingProject> pending,
        CancellationToken cancellationToken)
    {
        var packageRoot = CreateUniqueFolder(outputRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in pending)
            {
                var projectFolder = Path.Combine(packageRoot, UniqueName(SanitizeName(item.Result.ProjectName), usedNames));
                foreach (var relativePath in item.Result.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await CopyFileAsync(
                        Path.Combine(item.PublishFolder, relativePath),
                        Path.Combine(projectFolder, relativePath),
                        cancellationToken);
                }

                item.Result.PackageFolder = projectFolder;
            }

            return packageRoot;
        }
        catch
        {
            TryDeleteDirectory(packageRoot);
            throw;
        }
    }

    private static Task ScanAsync(
        string publishFolder,
        DateTime cutoffUtc,
        ExclusionRules exclusions,
        PackageProjectResult projectResult,
        CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(publishFolder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(publishFolder, file);
            if (exclusions.IsExcluded(relativePath))
            {
                continue;
            }

            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc >= cutoffUtc)
            {
                projectResult.Files.Add(relativePath);
                projectResult.RecentFiles++;
            }
            else
            {
                projectResult.OlderFiles++;
            }
        }

        return Task.CompletedTask;
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
    }

    private static string CreateUniqueFolder(string outputRoot, string name)
    {
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, name);
        for (var suffix = 2; Directory.Exists(path); suffix++)
        {
            path = Path.Combine(outputRoot, $"{name}_{suffix}");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static string UniqueName(string name, HashSet<string> usedNames)
    {
        var candidate = name;
        for (var suffix = 2; !usedNames.Add(candidate); suffix++)
        {
            candidate = $"{name}_{suffix}";
        }

        return candidate;
    }

    private static string SanitizeName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Project" : sanitized;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove incomplete package folder {PackageFolder}", path);
        }
    }

    private sealed record PendingProject(PackageProjectResult Result, string PublishFolder);

    private sealed class ExclusionRules(IReadOnlyList<(Regex Pattern, bool MatchesPath)> rules)
    {
        public static ExclusionRules Parse(string patterns)
        {
            var rules = new List<(Regex, bool)>();
            foreach (var raw in patterns.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pattern = raw.Replace('/', '\\').Trim('\\');
                if (pattern.Length == 0)
                {
                    continue;
                }

                var body = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
                var matchesPath = pattern.Contains('\\');
                var expression = matchesPath ? $"^{body}(\\\\.*)?$" : $"^{body}$";
                rules.Add((new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), matchesPath));
            }

            return new ExclusionRules(rules);
        }

        public bool IsExcluded(string relativePath)
        {
            var normalized = relativePath.Replace('/', '\\');
            var segments = normalized.Split('\\');
            return rules.Any(rule => rule.MatchesPath
                ? rule.Pattern.IsMatch(normalized)
                : segments.Any(rule.Pattern.IsMatch));
        }
    }
}
