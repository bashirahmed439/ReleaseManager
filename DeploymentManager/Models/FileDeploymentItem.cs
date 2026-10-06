namespace DeploymentManager.Models;

public sealed record FileDeploymentItem(string RelativePath, string SourcePath, string DestinationPath);