namespace DeploymentManager.Models;

public sealed class DeploymentConfiguration
{
    public string BackupRoot { get; set; } = string.Empty;
    public List<DeploymentMapping> Mappings { get; set; } = [];
    public DeploymentOptions DeploymentOptions { get; set; } = new();
    public LastDeploymentRecord LastDeployment { get; set; } = new();
    public PackageBuilderSettings PackageBuilder { get; set; } = new();

    public static DeploymentConfiguration CreateDefault() => new()
    {
        Mappings =
        [
            new() { Name = "UserManagmentAPI" },
            new() { Name = "GMS API" },
            new() { Name = "NSER API" },
            new() { Name = "Web App" }
        ]
    };
}

public sealed class LastDeploymentRecord
{
    public string DeploymentId { get; set; } = string.Empty;
    public DateTime? DeployedAtUtc { get; set; }
    public string Status { get; set; } = "None";
    public List<DeployedFileRecord> Files { get; set; } = [];
    public List<DeploymentApplicationRecord> Applications { get; set; } = [];
}

public sealed class DeploymentApplicationRecord
{
    public string ApplicationName { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public string BackupDirectory { get; set; } = string.Empty;
    public bool ManageIisAppPool { get; set; }
    public string IisServer { get; set; } = string.Empty;
    public string IisAppPoolName { get; set; } = string.Empty;
    public string Status { get; set; } = "Success";
    public List<string> CreatedFiles { get; set; } = [];
    public List<string> ChangedFiles { get; set; } = [];
    public List<string> BackedUpFiles { get; set; } = [];
    public DateTime? RolledBackAtUtc { get; set; }
}

public sealed class DeploymentOptions
{
    public bool HashComparisonEnabled { get; set; }
    public bool VerifyFilesAfterCopy { get; set; } = true;
    public int RetryAttempts { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 500;
}