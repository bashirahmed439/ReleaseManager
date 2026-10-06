namespace DeploymentManager.Services;

public interface IFolderPickerService
{
    string? PickFolder(string currentPath, string title);
}