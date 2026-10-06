namespace DeploymentManager.Models;

public sealed class RollbackApplicationResult
{
    public required string ApplicationName { get; init; }
    public string Status { get; set; } = "Running";
    public int RestoredFiles { get; set; }
    public int RemovedFiles { get; set; }
}