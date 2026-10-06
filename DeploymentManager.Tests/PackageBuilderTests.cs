using DeploymentManager.Models;
using DeploymentManager.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeploymentManager.Tests;

public sealed class PackageBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DeploymentManager.Packages-{Guid.NewGuid():N}");
    private string SolutionFolder => Path.Combine(_root, "solution");
    private string OutputFolder => Path.Combine(_root, "packages");

    public PackageBuilderTests() => Directory.CreateDirectory(SolutionFolder);

    [Fact]
    public async Task FilesPublishedWithinLastHour_ArePackagedAndKeepFolderStructure()
    {
        CreateSolution("Api");
        await PublishAsync("Api", "Api.dll", "v1");
        await PublishAsync("Api", Path.Combine("wwwroot", "js", "app.js"), "js");

        var result = await BuildAsync();

        Assert.NotNull(result.PackageRoot);
        Assert.Equal(2, result.TotalFiles);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(result.PackageRoot, "Api", "Api.dll")));
        Assert.Equal("js", await File.ReadAllTextAsync(Path.Combine(result.PackageRoot, "Api", "wwwroot", "js", "app.js")));
    }

    [Fact]
    public async Task FilesPublishedMoreThanAnHourAgo_AreExcluded()
    {
        CreateSolution("Api");
        await PublishAsync("Api", "recent.dll", "recent");
        await PublishAsync("Api", "stale.dll", "stale");
        File.SetLastWriteTimeUtc(Path.Combine(PublishFolder("Api"), "stale.dll"), DateTime.UtcNow.AddHours(-2));

        var result = await BuildAsync();

        var project = Assert.Single(result.Projects);
        Assert.Equal(["recent.dll"], project.Files);
        Assert.Equal(1, project.RecentFiles);
        Assert.Equal(1, project.OlderFiles);
        Assert.False(File.Exists(Path.Combine(result.PackageRoot!, "Api", "stale.dll")));
    }

    [Fact]
    public async Task OnlyOlderFiles_CreateNoPackage()
    {
        CreateSolution("Api");
        await PublishAsync("Api", "Api.dll", "v1");
        await PublishAsync("Api", "app.js", "js");
        foreach (var file in Directory.EnumerateFiles(PublishFolder("Api"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));
        }

        var result = await BuildAsync();

        Assert.Null(result.PackageRoot);
        Assert.Equal(2, result.Projects[0].OlderFiles);
    }

    [Fact]
    public async Task ExcludePatterns_SkipMatchingFilesAndFolders()
    {
        CreateSolution("Api");
        await PublishAsync("Api", "Api.dll", "v1");
        await PublishAsync("Api", "Api.pdb", "symbols");
        await PublishAsync("Api", Path.Combine("logs", "app.log"), "log");
        await PublishAsync("Api", Path.Combine("wwwroot", "uploads", "a.png"), "png");
        await PublishAsync("Api", Path.Combine("wwwroot", "site.css"), "css");

        var result = await BuildAsync(exclude: "*.pdb; logs; wwwroot\\uploads");

        Assert.Equal(["Api.dll", Path.Combine("wwwroot", "site.css")], result.Projects[0].Files.Order());
    }

    [Fact]
    public async Task ProjectWithoutPublishOutput_IsSkippedWhileOthersArePackaged()
    {
        CreateSolution("Api", "Library");
        await PublishAsync("Api", "Api.dll", "v1");

        var result = await BuildAsync();

        Assert.Equal(1, result.TotalFiles);
        Assert.NotNull(result.Projects.Single(project => project.ProjectName == "Library").SkippedReason);
        Assert.Null(result.Projects.Single(project => project.ProjectName == "Api").SkippedReason);
    }

    [Fact]
    public async Task PublishProfile_PublishUrlIsUsedWhenThereIsNoBinPublishFolder()
    {
        CreateSolution("Api");
        var profileFolder = Path.Combine(SolutionFolder, "Api", "Properties", "PublishProfiles");
        Directory.CreateDirectory(profileFolder);
        await File.WriteAllTextAsync(
            Path.Combine(profileFolder, "Folder.pubxml"),
            "<Project><PropertyGroup><PublishUrl>CustomPublish\\</PublishUrl></PropertyGroup></Project>");
        var customFolder = Path.Combine(SolutionFolder, "Api", "CustomPublish");
        Directory.CreateDirectory(customFolder);
        await File.WriteAllTextAsync(Path.Combine(customFolder, "Api.dll"), "custom");

        var result = await BuildAsync();

        Assert.Equal("custom", await File.ReadAllTextAsync(Path.Combine(result.PackageRoot!, "Api", "Api.dll")));
    }

    [Fact]
    public async Task ProjectsNotListedInTheSolutionAreIgnored()
    {
        CreateSolution("Api");
        CreateProjectFile("Stray");
        await PublishAsync("Api", "Api.dll", "v1");
        await PublishAsync("Stray", "Stray.dll", "x");

        var result = await BuildAsync();

        Assert.Equal(["Api"], result.Projects.Select(project => project.ProjectName));
    }

    [Fact]
    public async Task CancelledBuild_LeavesNoPackage()
    {
        CreateSolution("Api");
        await PublishAsync("Api", "Api.dll", "v1");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildAsync(cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(OutputFolder) && Directory.EnumerateDirectories(OutputFolder).Any());
        var afterCancel = await BuildAsync();

        Assert.Single(Directory.EnumerateDirectories(OutputFolder));
        Assert.Equal(["Api.dll"], afterCancel.Projects[0].Files);
    }

    [Fact]
    public async Task OutputFolderInsidePublishFolder_IsRejected()
    {
        CreateSolution("Api");
        await PublishAsync("Api", "Api.dll", "v1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().BuildAsync(
                new PackageBuilderSettings { SolutionFolder = SolutionFolder, OutputFolder = Path.Combine(PublishFolder("Api"), "out") },
                includeAllFiles: false));
    }

    [Fact]
    public void ProjectLocator_WithoutSolutionFile_ScansForProjectsAndSkipsBuildFolders()
    {
        CreateProjectFile("Web");
        var objFolder = Path.Combine(SolutionFolder, "Web", "obj");
        Directory.CreateDirectory(objFolder);
        File.WriteAllText(Path.Combine(objFolder, "Generated.csproj"), "<Project />");

        var projects = SolutionProjectLocator.FindProjects(SolutionFolder);

        Assert.Equal(["Web"], projects.Select(project => project.Name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string PublishFolder(string project) =>
        Path.Combine(SolutionFolder, project, "bin", "Release", "net8.0", "publish");

    private void CreateSolution(params string[] projects)
    {
        var lines = new List<string> { "Microsoft Visual Studio Solution File, Format Version 12.00" };
        foreach (var project in projects)
        {
            CreateProjectFile(project);
            lines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{project}\", \"{project}\\{project}.csproj\", \"{{{Guid.NewGuid()}}}\"");
            lines.Add("EndProject");
        }

        File.WriteAllLines(Path.Combine(SolutionFolder, "Test.sln"), lines);
    }

    private void CreateProjectFile(string project)
    {
        Directory.CreateDirectory(Path.Combine(SolutionFolder, project));
        File.WriteAllText(Path.Combine(SolutionFolder, project, $"{project}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    private async Task PublishAsync(string project, string relativePath, string content)
    {
        var path = Path.Combine(PublishFolder(project), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private Task<PackageBuildResult> BuildAsync(
        string exclude = "",
        CancellationToken cancellationToken = default) =>
        CreateService().BuildAsync(
            new PackageBuilderSettings { SolutionFolder = SolutionFolder, OutputFolder = OutputFolder, ExcludePatterns = exclude },
            includeAllFiles: false,
            cancellationToken: cancellationToken);

    private PackageBuilderService CreateService() => new(NullLogger<PackageBuilderService>.Instance);
}
