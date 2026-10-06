namespace DeploymentManager.Models;

public sealed class DeployedFileRecord
{
    public string ApplicationName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
}