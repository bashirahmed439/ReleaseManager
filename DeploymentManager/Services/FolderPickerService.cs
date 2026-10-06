using System.IO;
using Microsoft.Win32;

namespace DeploymentManager.Services;

public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string currentPath, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = currentPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}