namespace DeploymentManager.Models;

public sealed record DeploymentProgress(
    string ApplicationName,
    string CurrentFile,
    int CompletedFiles,
    int TotalFiles,
    string Message)
{
    public double Percentage => TotalFiles == 0 ? 100 : (double)CompletedFiles / TotalFiles * 100;
}