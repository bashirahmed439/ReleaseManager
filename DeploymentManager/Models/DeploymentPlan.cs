namespace DeploymentManager.Models;

public sealed class DeploymentPlan
{
    public required DeploymentMapping Mapping { get; init; }
    public List<FileDeploymentItem> NewFiles { get; } = [];
    public List<FileDeploymentItem> ChangedFiles { get; } = [];
    public List<FileDeploymentItem> UnchangedFiles { get; } = [];
    public int FilesToCopy => NewFiles.Count + ChangedFiles.Count;
    public int BackupFileCount { get; set; }
}