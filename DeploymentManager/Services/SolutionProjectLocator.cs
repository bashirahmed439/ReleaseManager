using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DeploymentManager.Services;

public sealed record SolutionProject(string Name, string ProjectFile, string ProjectDirectory);

public static partial class SolutionProjectLocator
{
    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj", ".fsproj"];
    private static readonly HashSet<string> SkippedDirectories =
        new(["bin", "obj", "node_modules", ".git", ".vs", "packages"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"Project\(""\{[0-9A-Fa-f-]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""")]
    private static partial Regex SlnProjectPattern();

    [GeneratedRegex(@"<Project\s+Path=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SlnxProjectPattern();

    public static IReadOnlyList<SolutionProject> FindProjects(string solutionFolder)
    {
        var root = Path.GetFullPath(solutionFolder);
        var projectFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var solutionFile in Directory.EnumerateFiles(root)
                     .Where(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                         || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var relativePath in ReadProjectPaths(solutionFile))
            {
                var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (IsProjectFile(fullPath) && File.Exists(fullPath))
                {
                    projectFiles.Add(fullPath);
                }
            }
        }

        if (projectFiles.Count == 0)
        {
            foreach (var projectFile in EnumerateProjectFiles(root))
            {
                projectFiles.Add(projectFile);
            }
        }

        return projectFiles
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => new SolutionProject(Path.GetFileNameWithoutExtension(file), file, Path.GetDirectoryName(file)!))
            .ToArray();
    }

    // Picks the most recently written publish output, from .pubxml PublishUrl values and bin\**\publish folders.
    public static string? FindPublishFolder(SolutionProject project)
    {
        var candidates = new List<string>();
        var profileFolder = Path.Combine(project.ProjectDirectory, "Properties", "PublishProfiles");
        if (Directory.Exists(profileFolder))
        {
            foreach (var profile in Directory.EnumerateFiles(profileFolder, "*.pubxml"))
            {
                if (TryReadPublishFolder(profile, project.ProjectDirectory) is { } folder)
                {
                    candidates.Add(folder);
                }
            }
        }

        var binFolder = Path.Combine(project.ProjectDirectory, "bin");
        if (Directory.Exists(binFolder))
        {
            candidates.AddRange(Directory.EnumerateDirectories(binFolder, "publish", SearchOption.AllDirectories));
        }

        var distinct = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct
            .Where(candidate => !distinct.Any(other => !other.Equals(candidate, StringComparison.OrdinalIgnoreCase) && IsInside(candidate, other)))
            .Select(candidate => (Folder: candidate, Newest: NewestWriteTimeUtc(candidate)))
            .Where(candidate => candidate.Newest is not null)
            .OrderByDescending(candidate => candidate.Newest)
            .Select(candidate => candidate.Folder)
            .FirstOrDefault();
    }

    internal static bool IsInside(string child, string parent)
    {
        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var childPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + Path.DirectorySeparatorChar;
        return childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadProjectPaths(string solutionFile)
    {
        var content = File.ReadAllText(solutionFile);
        var pattern = solutionFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? SlnxProjectPattern() : SlnProjectPattern();
        return pattern.Matches(content).Select(match => match.Groups[1].Value);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory).Where(IsProjectFile))
            {
                yield return file;
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(subdirectory)))
                {
                    pending.Push(subdirectory);
                }
            }
        }
    }

    private static bool IsProjectFile(string path) =>
        ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string? TryReadPublishFolder(string profilePath, string projectDirectory)
    {
        try
        {
            var publishUrl = XDocument.Load(profilePath)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "PublishUrl")?.Value.Trim();
            if (string.IsNullOrEmpty(publishUrl) || publishUrl.Contains("$(", StringComparison.Ordinal))
            {
                return null;
            }

            if (Uri.TryCreate(publishUrl, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                return null;
            }

            var folder = Path.GetFullPath(Path.Combine(projectDirectory, publishUrl));
            return Directory.Exists(folder) ? Path.TrimEndingDirectorySeparator(folder) : null;
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTime? NewestWriteTimeUtc(string folder)
    {
        DateTime? newest = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (newest is null || written > newest)
            {
                newest = written;
            }
        }

        return newest;
    }
}
