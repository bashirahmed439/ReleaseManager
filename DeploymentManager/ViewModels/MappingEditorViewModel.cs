using DeploymentManager.Models;
using DeploymentManager.Services;

namespace DeploymentManager.ViewModels;

public sealed class MappingEditorViewModel : ObservableObject
{
    private readonly IFolderPickerService _folderPicker;
    private readonly DeploymentMapping _mapping;
    private string _name;
    private string _sourceFolder;
    private string _destinationFolder;
    private bool _backupEnabled;
    private bool _javascriptScaffoldingEnabled;
    private bool _enabled;
    private bool _manageIisAppPool;
    private string _iisServer;
    private string _iisAppPoolName;
    private string _validationMessage = string.Empty;

    public MappingEditorViewModel(DeploymentMapping mapping, IFolderPickerService folderPicker)
    {
        _mapping = mapping;
        _folderPicker = folderPicker;
        _name = mapping.Name;
        _sourceFolder = mapping.SourceFolder;
        _destinationFolder = mapping.DestinationFolder;
        _backupEnabled = mapping.BackupEnabled;
        _javascriptScaffoldingEnabled = mapping.JavaScriptScaffoldingEnabled;
        _enabled = mapping.Enabled;
        _manageIisAppPool = mapping.ManageIisAppPool;
        _iisServer = mapping.IisServer;
        _iisAppPoolName = mapping.IisAppPoolName;
        BrowseSourceCommand = new RelayCommand(() => SourceFolder = Browse(SourceFolder, "Select source folder"));
        BrowseDestinationCommand = new RelayCommand(() => DestinationFolder = Browse(DestinationFolder, "Select destination folder"));
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => OnRequestClose?.Invoke(false));
    }

    public event Action<bool>? OnRequestClose;
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseDestinationCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string SourceFolder { get => _sourceFolder; set => SetProperty(ref _sourceFolder, value); }
    public string DestinationFolder { get => _destinationFolder; set => SetProperty(ref _destinationFolder, value); }
    public bool BackupEnabled { get => _backupEnabled; set => SetProperty(ref _backupEnabled, value); }
    public bool JavaScriptScaffoldingEnabled { get => _javascriptScaffoldingEnabled; set => SetProperty(ref _javascriptScaffoldingEnabled, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool ManageIisAppPool { get => _manageIisAppPool; set => SetProperty(ref _manageIisAppPool, value); }
    public string IisServer { get => _iisServer; set => SetProperty(ref _iisServer, value); }
    public string IisAppPoolName { get => _iisAppPoolName; set => SetProperty(ref _iisAppPoolName, value); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

    public bool TryCreateMapping(out DeploymentMapping? mapping)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = "Enter an application name.";
            mapping = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(SourceFolder) || string.IsNullOrWhiteSpace(DestinationFolder))
        {
            ValidationMessage = "Choose both a source and destination folder.";
            mapping = null;
            return false;
        }

        if (ManageIisAppPool && string.IsNullOrWhiteSpace(IisAppPoolName))
        {
            ValidationMessage = "Enter the IIS application pool name.";
            mapping = null;
            return false;
        }

        ValidationMessage = string.Empty;
        mapping = CreateMapping();
        return true;
    }

    public DeploymentMapping CreateMapping() => new()
    {
        Name = Name.Trim(),
        SourceFolder = SourceFolder.Trim(),
        DestinationFolder = DestinationFolder.Trim(),
        BackupEnabled = BackupEnabled,
        JavaScriptScaffoldingEnabled = JavaScriptScaffoldingEnabled,
        Enabled = Enabled,
        ManageIisAppPool = ManageIisAppPool,
        IisServer = IisServer.Trim(),
        IisAppPoolName = IisAppPoolName.Trim(),
        BackupExclusions = [.. _mapping.BackupExclusions]
    };

    private string Browse(string currentPath, string title) =>
        _folderPicker.PickFolder(currentPath, title) ?? currentPath;

    private void Save()
    {
        if (TryCreateMapping(out _))
        {
            OnRequestClose?.Invoke(true);
        }
    }
}