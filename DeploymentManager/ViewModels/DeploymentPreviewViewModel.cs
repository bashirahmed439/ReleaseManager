using DeploymentManager.Models;

namespace DeploymentManager.ViewModels;

public sealed class DeploymentPreviewViewModel(IReadOnlyList<DeploymentPlan> plans)
{
    public IReadOnlyList<DeploymentPreviewApplication> Applications { get; } = plans.Select(plan => new DeploymentPreviewApplication(
        plan.Mapping.Name,
        GetIisTarget(plan.Mapping),
        plan.Mapping.DestinationFolder,
        plan.NewFiles.Count,
        plan.ChangedFiles.Count,
        plan.UnchangedFiles.Count,
        plan.BackupFileCount)).ToArray();

    private static string GetIisTarget(DeploymentMapping mapping) => !mapping.ManageIisAppPool
        ? "Not managed"
        : $"{(string.IsNullOrWhiteSpace(mapping.IisServer) ? "Local" : mapping.IisServer)} / {mapping.IisAppPoolName}";

    public int TotalNewFiles => Applications.Sum(application => application.NewFiles);
    public int TotalChangedFiles => Applications.Sum(application => application.ChangedFiles);
    public int TotalUnchangedFiles => Applications.Sum(application => application.UnchangedFiles);
    public int TotalBackupFiles => Applications.Sum(application => application.BackupFiles);
}