using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeploymentManager.Models;

public sealed class DeploymentMapping : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _sourceFolder = string.Empty;
    private string _destinationFolder = string.Empty;
    private bool _backupEnabled = true;
    private bool _javascriptScaffoldingEnabled = true;
    private bool _enabled = true;
    private bool _selectedForDeployment;
    private bool _manageIisAppPool;
    private string _iisServer = string.Empty;
    private string _iisAppPoolName = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string SourceFolder { get => _sourceFolder; set => SetProperty(ref _sourceFolder, value); }
    public string DestinationFolder { get => _destinationFolder; set => SetProperty(ref _destinationFolder, value); }
    public bool BackupEnabled { get => _backupEnabled; set => SetProperty(ref _backupEnabled, value); }
    public bool JavaScriptScaffoldingEnabled { get => _javascriptScaffoldingEnabled; set => SetProperty(ref _javascriptScaffoldingEnabled, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool ManageIisAppPool { get => _manageIisAppPool; set => SetProperty(ref _manageIisAppPool, value); }
    public string IisServer { get => _iisServer; set => SetProperty(ref _iisServer, value); }
    public string IisAppPoolName { get => _iisAppPoolName; set => SetProperty(ref _iisAppPoolName, value); }
    public List<string> BackupExclusions { get; set; } = [];

    [JsonIgnore]
    public bool SelectedForDeployment { get => _selectedForDeployment; set => SetProperty(ref _selectedForDeployment, value); }

    public DeploymentMapping Copy() => new()
    {
        Name = Name,
        SourceFolder = SourceFolder,
        DestinationFolder = DestinationFolder,
        BackupEnabled = BackupEnabled,
        JavaScriptScaffoldingEnabled = JavaScriptScaffoldingEnabled,
        Enabled = Enabled,
        ManageIisAppPool = ManageIisAppPool,
        IisServer = IisServer,
        IisAppPoolName = IisAppPoolName,
        BackupExclusions = [.. BackupExclusions],
        SelectedForDeployment = SelectedForDeployment
    };

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}