namespace DeploymentManager.Services;

public interface IIniConfigurationMerger
{
    Task<int> CountMissingEntriesAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    Task<int> AppendMissingEntriesAsync(
        string sourcePath,
        string destinationPath,
        Func<byte[], Task>? beforeAppend = null,
        CancellationToken cancellationToken = default);
}