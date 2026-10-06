namespace DeploymentManager.Models;

public sealed class DeploymentResult
{
    public required string ApplicationName { get; init; }
    public required string Status { get; set; }
    public required string BackupPath { get; set; }
    public int NewFilesCopied { get; set; }
    public int ChangedFilesCopied { get; set; }
    public int UnchangedFilesSkipped { get; set; }
    public List<string> DeployedFiles { get; } = [];
    public List<string> CreatedFiles { get; } = [];
    public List<string> ChangedFiles { get; } = [];
    public List<string> BackedUpFiles { get; } = [];
    public string? ErrorMessage { get; set; }
}