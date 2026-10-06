# Deployment Manager

Deployment Manager is a Windows WPF utility for configuring reusable application folder mappings and deploying only new or changed files. It previews changes before deployment, backs up existing changed files when enabled, verifies staged copies, and never deletes destination-only files.

## Requirements

- Windows 10 or later
- .NET 8 SDK to build from source
- Visual Studio Code with the C# Dev Kit extension, or Visual Studio

## Build and run

From this directory:

```powershell
dotnet restore DeploymentManager.sln
dotnet build DeploymentManager.sln
dotnet test DeploymentManager.sln
dotnet run --project DeploymentManager/DeploymentManager.csproj
```

## Mapping configuration

The first launch creates `%LOCALAPPDATA%\DeploymentManager\settings.json` with four editable example mapping names and no assumed folder paths. Add or edit mappings to select a source and destination folder. Configure the global backup root on the main screen. Mapping and backup-root changes are saved after add, edit, remove, or browsing for the backup root; use **Save settings** after changing fields directly.

The JSON file is user-specific and is not written beside the executable. It contains mapping names and paths, per-mapping backup and JavaScript-scaffolding flags, enablement, backup exclusions, and the global backup root. It must not be used to store passwords, tokens, or other secrets.

Example:

```json
{
  "backupRoot": "D:\\DeploymentBackups",
  "mappings": [
    {
      "name": "Example API",
      "sourceFolder": "D:\\Projects\\ExampleApi\\publish",
      "destinationFolder": "\\\\SERVER01\\D$\\Applications\\ExampleApi",
      "backupEnabled": true,
      "javascriptScaffoldingEnabled": true,
      "enabled": true,
      "backupExclusions": []
    }
  ]
}
```

## Deploy files

Select configured mappings and choose **Preview changes**. The preview shows new, modified, unchanged, and backup file counts per application. Confirming the preview starts deployments sequentially. Progress and per-file activity appear in the main window; deployment can be cancelled. A preview is rescanned before copying, and deployment stops if the plan has changed since confirmation.

Comparison uses file size and last-write time by default. Optional SHA-256 comparison can be enabled in `settings.json` under `deploymentOptions`. Changed files are copied to a temporary file beside the destination and verified before replacement. Existing changed destination files are backed up first when mapping backup is enabled; new files are not backed up. Relative backup exclusions match both the named path and files below it. JavaScript files use the isolated scaffolding service, whose initial implementation copies them unchanged.

Files named `configuration.ini` (case-insensitive, at any depth) use append-only merging instead of replacement when the destination file already exists. Section and key names are matched case-insensitively. Existing destination values are authoritative; only missing source keys are appended, and a changed source value for an existing key is ignored. If the destination contains sections and the source introduces a new unsectioned key, preview stops with an error rather than appending it under the wrong section. A new `configuration.ini` is copied normally when no destination file exists.

Destination-only files are never deleted during deployment. A failed or cancelled copy leaves its staged file out of the destination; any completed backups are retained. Locked files are retried three times by default. Logging is structured through `Microsoft.Extensions.Logging` and shown in the application; rolling per-user log files and long-term deployment history are not implemented yet. Credentials are not stored in JSON.

For IIS-hosted projects, enable **Stop and restart an IIS application pool during deployment** in the mapping editor and provide the pool name. Leave the server blank to manage IIS on the current computer; for a remote server, enter its computer name. A running pool is stopped before files are changed and restarted afterward, including when deployment is cancelled or fails. Pools already stopped are left stopped, and IIS is not touched when the preview has no files to deploy. A restart failure is reported as a critical deployment error and requires manually starting the pool.

The current Windows account must have permission to manage the pool. Local control requires IIS and the WebAdministration PowerShell module on the deployment computer; remote control uses PowerShell remoting/WinRM and the current Windows identity. No credentials are saved by the application. Run the app elevated if required for local IIS permissions; remote hosts must allow the account to manage the configured pool.

## Packages

The **Packages** tab collects updated files after a Visual Studio publish. Choose the solution folder and a package output folder, then click **Create deployable folders**. Projects are read from the `.sln` or `.slnx` file (or found by scanning for project files when there is none). Each project's most recently written publish output is used: a folder from its `.pubxml` `PublishUrl`, or `bin\**\publish`. Projects with no publish output are skipped and reported.

Only files whose publish-output last-write time is within the last hour are copied, preserving folder structure, into `<output>\yyyy-MM-dd_HH-mm-ss\<ProjectName>\`. If no files were published in that window, no folder is created.

**Exclude files** accepts `;`-separated patterns: `*.pdb` matches file names, `logs` matches any folder or file with that name, and `wwwroot\uploads` matches a relative path and everything below it. A failed or cancelled run removes its partial folder. Point a deployment mapping's source folder at a project folder inside a package to deploy it.

## Rollback

After each deployment, the latest-deployment record in `%LOCALAPPDATA%\DeploymentManager\settings.json` stores a per-application manifest: destination, files created, files changed, and files whose originals were backed up. Use the undo icon on a project tile to roll back only that API, or **Roll back latest deployment** on the Last deployment page to roll back all APIs with pending changes.

Rollback validates all selected applications before making changes. It restores overwritten files from their recorded backups and removes only files recorded as newly created by that deployment; unrelated destination files are untouched. If any overwritten file has no backup (for example, backup was disabled or the file was excluded), rollback refuses that selected scope rather than guessing. Whole-deployment rollback preflights every API first; if any API is not reversible, roll back reversible APIs individually. Successful per-API rollback state is persisted, so a partial rollback can be resumed later. When IIS management is enabled, rollback also stops a running pool and attempts to restart it afterward.

`SelectedForDeployment` is a UI-only selection and is not persisted. Runtime defaults for deployment behavior can be configured in JSON:

```json
"deploymentOptions": {
  "hashComparisonEnabled": false,
  "verifyFilesAfterCopy": true,
  "retryAttempts": 3,
  "retryDelayMilliseconds": 500
}
```

The deployment engine is separated behind planner, executor, and JavaScript-scaffolding interfaces so it can be reused outside the WPF view layer.

## Publish

Create a self-contained Windows x64 publish output with:

```powershell
dotnet publish DeploymentManager/DeploymentManager.csproj -c Release -r win-x64 --self-contained true
```

The output is written under `DeploymentManager/bin/Release/net8.0-windows/win-x64/publish`. The project targets .NET 8 and Windows x64; self-contained publish includes the runtime so the target machine does not need a separate .NET installation.

## Build installer

Install Inno Setup 6, then run from the repository root:

```powershell
./build-installer.ps1
```

The script publishes a self-contained Windows x64 build and compiles `installer/DeploymentManager.iss`. The setup executable is created at `artifacts/installer/DeploymentManager-Setup-1.0.0-x64.exe`; pass `-Version 1.2.0` to set another installer version. Inno Setup can be installed per-user; if it is in a custom location, pass `-CompilerPath "C:\path\to\ISCC.exe"`.

The installer installs per-user under `%LOCALAPPDATA%\Programs\Deployment Manager`, adds a Start Menu shortcut, and offers an optional desktop shortcut. It does not install prerequisites because the application is self-contained. Settings and deployment data remain under `%LOCALAPPDATA%\DeploymentManager` and are not bundled in the installer or removed by uninstall.