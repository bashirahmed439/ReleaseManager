namespace DeploymentManager.Models;

public sealed record DeploymentPreviewApplication(
    string Name,
    string IisTarget,
    string DestinationFolder,
    int NewFiles,
    int ChangedFiles,
    int UnchangedFiles,
    int BackupFiles);