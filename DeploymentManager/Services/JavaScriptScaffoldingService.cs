using System.IO;
using DeploymentManager.Models;

namespace DeploymentManager.Services;

public sealed class JavaScriptScaffoldingService : IJavaScriptScaffoldingService
{
    public async Task ProcessAsync(
        string sourceFile,
        string destinationFile,
        DeploymentMapping mapping,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destination = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
    }
}