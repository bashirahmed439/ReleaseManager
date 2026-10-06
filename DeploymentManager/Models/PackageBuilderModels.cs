namespace DeploymentManager.Models;

public sealed class PackageBuilderSettings
{
    public string SolutionFolder { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public string ExcludePatterns { get; set; } = string.Empty;
}

public sealed class PackageProjectResult
{
    public required string ProjectName { get; init; }
    public string PublishFolder { get; set; } = string.Empty;
    public string PackageFolder { get; set; } = string.Empty;
    public string? SkippedReason { get; set; }
    public int RecentFiles { get; set; }
    public int OlderFiles { get; set; }
    public List<string> Files { get; } = [];

    public string Summary => SkippedReason
        ?? (Files.Count == 0
            ? $"No files published in the last hour ({OlderFiles} older file(s))"
            : $"{RecentFiles} file(s) published in the last hour");
}

public sealed class PackageBuildResult
{
    public string? PackageRoot { get; set; }
    public List<PackageProjectResult> Projects { get; } = [];
    public int TotalFiles => Projects.Sum(project => project.Files.Count);
}
