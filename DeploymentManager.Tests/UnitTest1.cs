using DeploymentManager.Models;
using DeploymentManager.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;

namespace DeploymentManager.Tests;

public sealed class DeploymentEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DeploymentManager.Tests-{Guid.NewGuid():N}");
    private string Source => Path.Combine(_root, "source");
    private string Destination => Path.Combine(_root, "destination");
    private string Backup => Path.Combine(_root, "backup");

    public DeploymentEngineTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
    }

    [Fact]
    public async Task Planner_ClassifiesNewChangedUnchanged_AndLeavesDestinationOnlyFilesAlone()
    {
        await WriteAsync(Source, "new.txt", "new content");
        await WriteAsync(Source, "changed.txt", "new version");
        await WriteAsync(Source, "same.txt", "same");
        await WriteAsync(Destination, "changed.txt", "old version");
        await WriteAsync(Destination, "same.txt", "same");
        await WriteAsync(Destination, "removed-from-source.txt", "keep me");
        File.SetLastWriteTimeUtc(Path.Combine(Source, "changed.txt"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(Destination, "changed.txt"), DateTime.UtcNow.AddMinutes(-2));
        var sameTimestamp = DateTime.UtcNow.AddMinutes(-2);
        File.SetLastWriteTimeUtc(Path.Combine(Source, "same.txt"), sameTimestamp);
        File.SetLastWriteTimeUtc(Path.Combine(Destination, "same.txt"), sameTimestamp);

        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);

        Assert.Equal(["new.txt"], plan.NewFiles.Select(file => file.RelativePath));
        Assert.Equal(["changed.txt"], plan.ChangedFiles.Select(file => file.RelativePath));
        Assert.Equal(["same.txt"], plan.UnchangedFiles.Select(file => file.RelativePath));
        Assert.Equal(1, plan.BackupFileCount);
    }

    [Fact]
    public async Task Planner_HashComparisonDetectsDifferentContentWithMatchingMetadata()
    {
        await WriteAsync(Source, "file.txt", "one");
        await WriteAsync(Destination, "file.txt", "two");
        var timestamp = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(Path.Combine(Source, "file.txt"), timestamp);
        File.SetLastWriteTimeUtc(Path.Combine(Destination, "file.txt"), timestamp);

        var withoutHash = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);
        var withHash = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: true);

        Assert.Single(withoutHash.UnchangedFiles);
        Assert.Single(withHash.ChangedFiles);
    }

    [Fact]
    public async Task Deploy_CopiesNewAndChanged_BacksUpOnlyOverwrittenFiles_AndKeepsDestinationOnlyFiles()
    {
        await WriteAsync(Source, "new.txt", "new content");
        await WriteAsync(Source, "changed.txt", "new version");
        await WriteAsync(Source, "same.txt", "same");
        await WriteAsync(Destination, "changed.txt", "old");
        await WriteAsync(Destination, "same.txt", "same");
        await WriteAsync(Destination, "destination-only.txt", "keep me");
        var sameTimestamp = DateTime.UtcNow.AddMinutes(-2);
        File.SetLastWriteTimeUtc(Path.Combine(Source, "same.txt"), sameTimestamp);
        File.SetLastWriteTimeUtc(Path.Combine(Destination, "same.txt"), sameTimestamp);

        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);
        var results = await CreateDeploymentService().DeployAsync([plan], Backup, FastOptions());

        Assert.Equal("new content", await File.ReadAllTextAsync(Path.Combine(Destination, "new.txt")));
        Assert.Equal("new version", await File.ReadAllTextAsync(Path.Combine(Destination, "changed.txt")));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(Destination, "destination-only.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(results[0].BackupPath, "changed.txt")));
        Assert.False(File.Exists(Path.Combine(results[0].BackupPath, "new.txt")));
        Assert.Equal(1, results[0].NewFilesCopied);
        Assert.Equal(1, results[0].ChangedFilesCopied);
        Assert.Equal(1, results[0].UnchangedFilesSkipped);
        Assert.Contains("new.txt", results[0].DeployedFiles);
        Assert.Contains("changed.txt", results[0].DeployedFiles);
        Assert.Contains("new.txt", results[0].CreatedFiles);
        Assert.Contains("changed.txt", results[0].ChangedFiles);
        Assert.Contains("changed.txt", results[0].BackedUpFiles);
    }

    [Fact]
    public async Task Deploy_StopsRunningIisPoolBeforeCopyAndRestartsAfterSuccess()
    {
        await WriteAsync(Source, "new.txt", "new content");
        var mapping = CreateMapping();
        mapping.ManageIisAppPool = true;
        mapping.IisAppPoolName = "ExamplePool";
        var plan = await CreatePlanner().CreatePlanAsync(mapping, hashComparisonEnabled: false);
        var iisService = new FakeIisApplicationPoolService();

        await CreateDeploymentService(iisService).DeployAsync([plan], Backup, FastOptions());

        Assert.Equal(["stop", "start"], iisService.Calls);
        Assert.True(iisService.IsStoppedDuringStart);
        Assert.True(iisService.RestartUsedNonCancelledToken);
        Assert.False(iisService.IsStopped);
        Assert.True(File.Exists(Path.Combine(Destination, "new.txt")));
    }

    [Fact]
    public async Task Deploy_DoesNotStartIisPoolThatWasAlreadyStopped()
    {
        await WriteAsync(Source, "new.txt", "new content");
        var mapping = CreateMapping();
        mapping.ManageIisAppPool = true;
        mapping.IisAppPoolName = "ExamplePool";
        var plan = await CreatePlanner().CreatePlanAsync(mapping, hashComparisonEnabled: false);
        var iisService = new FakeIisApplicationPoolService { WasRunning = false };

        await CreateDeploymentService(iisService).DeployAsync([plan], Backup, FastOptions());

        Assert.Equal(["stop"], iisService.Calls);
        Assert.False(iisService.IsStoppedDuringStart);
    }

    [Fact]
    public async Task Deploy_RestartsIisPoolWhenCancelledAfterStopping()
    {
        await WriteAsync(Source, "new.txt", "new content");
        var mapping = CreateMapping();
        mapping.ManageIisAppPool = true;
        mapping.IisAppPoolName = "ExamplePool";
        var plan = await CreatePlanner().CreatePlanAsync(mapping, hashComparisonEnabled: false);
        using var cancellation = new CancellationTokenSource();
        var iisService = new FakeIisApplicationPoolService { AfterStop = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateDeploymentService(iisService).DeployAsync([plan], Backup, FastOptions(), cancellationToken: cancellation.Token));

        Assert.Equal(["stop", "start"], iisService.Calls);
        Assert.False(iisService.IsStopped);
        Assert.True(iisService.RestartUsedNonCancelledToken);
    }

    [Fact]
    public async Task Deploy_ReportsIisRestartFailureAfterFilesAreCopied()
    {
        await WriteAsync(Source, "new.txt", "new content");
        var mapping = CreateMapping();
        mapping.ManageIisAppPool = true;
        mapping.IisAppPoolName = "ExamplePool";
        var plan = await CreatePlanner().CreatePlanAsync(mapping, hashComparisonEnabled: false);
        var iisService = new FakeIisApplicationPoolService { FailToStart = true };

        await Assert.ThrowsAsync<IisApplicationPoolRestartException>(() =>
            CreateDeploymentService(iisService).DeployAsync([plan], Backup, FastOptions()));

        Assert.True(File.Exists(Path.Combine(Destination, "new.txt")));
        Assert.True(iisService.IsStopped);
    }

    [Fact]
    public async Task Deploy_DoesNotStopIisWhenThereAreNoFilesToCopy()
    {
        await WriteAsync(Source, "same.txt", "same");
        await WriteAsync(Destination, "same.txt", "same");
        var timestamp = DateTime.UtcNow.AddMinutes(-2);
        File.SetLastWriteTimeUtc(Path.Combine(Source, "same.txt"), timestamp);
        File.SetLastWriteTimeUtc(Path.Combine(Destination, "same.txt"), timestamp);
        var mapping = CreateMapping();
        mapping.ManageIisAppPool = true;
        mapping.IisAppPoolName = "ExamplePool";
        var plan = await CreatePlanner().CreatePlanAsync(mapping, hashComparisonEnabled: false);
        var iisService = new FakeIisApplicationPoolService();

        await CreateDeploymentService(iisService).DeployAsync([plan], Backup, FastOptions());

        Assert.Empty(iisService.Calls);
    }

    [Fact]
    public void IisServerDetection_UsesLocalControlForConfiguredComputerName()
    {
        Assert.True(PowerShellIisApplicationPoolService.IsLocalServer(Environment.MachineName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData(".")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IisServerDetection_UsesLocalControlForLoopbackAliases(string? serverName)
    {
        Assert.True(PowerShellIisApplicationPoolService.IsLocalServer(serverName));
    }

    [Fact]
    public void IisServerDetection_LeavesRemoteNamesForPowerShellRemoting()
    {
        Assert.False(PowerShellIisApplicationPoolService.IsLocalServer("server-that-does-not-exist.invalid"));
    }

    [Fact]
    public async Task Rollback_RestoresOriginalsAndRemovesOnlyFilesCreatedByTheDeployment()
    {
        await WriteAsync(Destination, "changed.txt", "deployed version");
        await WriteAsync(Destination, "new.txt", "new deployment file");
        await WriteAsync(Destination, "unrelated.txt", "keep this file");
        var appBackup = Path.Combine(Backup, "deployment-1", "Test App");
        await WriteAsync(appBackup, "changed.txt", "original version");
        var record = CreateRollbackRecord("Test App", appBackup);
        var iisService = new FakeIisApplicationPoolService();

        var results = await CreateRollbackService(iisService).RollbackAsync(record);

        Assert.Equal("original version", await File.ReadAllTextAsync(Path.Combine(Destination, "changed.txt")));
        Assert.False(File.Exists(Path.Combine(Destination, "new.txt")));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(Path.Combine(Destination, "unrelated.txt")));
        Assert.Equal(1, results[0].RestoredFiles);
        Assert.Equal(1, results[0].RemovedFiles);
        Assert.NotNull(record.Applications[0].RolledBackAtUtc);
        Assert.Equal("Rolled back", record.Applications[0].Status);
        Assert.Equal("Rolled back", record.Status);
        Assert.Equal(["stop", "start"], iisService.Calls);
    }

    [Fact]
    public async Task Rollback_PreflightsAllApplicationsBeforeChangingAnyFiles()
    {
        await WriteAsync(Destination, "changed.txt", "deployed version");
        var firstBackup = Path.Combine(Backup, "deployment-1", "First API");
        await WriteAsync(firstBackup, "changed.txt", "first original");
        var secondDestination = Path.Combine(_root, "second-destination");
        Directory.CreateDirectory(secondDestination);
        await WriteAsync(secondDestination, "changed.txt", "second deployed version");
        var record = new LastDeploymentRecord
        {
            Applications =
            [
                CreateRollbackApplication("First API", Destination, firstBackup, ["changed.txt"], ["changed.txt"]),
                CreateRollbackApplication("Second API", secondDestination, Path.Combine(Backup, "missing"), ["changed.txt"], ["changed.txt"])
            ]
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() => CreateRollbackService().RollbackAsync(record));

        Assert.Equal("deployed version", await File.ReadAllTextAsync(Path.Combine(Destination, "changed.txt")));
        Assert.Equal("second deployed version", await File.ReadAllTextAsync(Path.Combine(secondDestination, "changed.txt")));
        Assert.Null(record.Applications[0].RolledBackAtUtc);
    }

    [Fact]
    public async Task Rollback_RefusesToOverwriteFilesWithoutOriginalBackups()
    {
        await WriteAsync(Destination, "excluded.txt", "deployed value");
        var record = new LastDeploymentRecord
        {
            Applications =
            [
                CreateRollbackApplication("Test App", Destination, string.Empty, ["excluded.txt"], [])
            ]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRollbackService().RollbackAsync(record));

        Assert.Equal("deployed value", await File.ReadAllTextAsync(Path.Combine(Destination, "excluded.txt")));
    }

    [Fact]
    public async Task Rollback_OneApiLeavesWholeDeploymentPartiallyRolledBack()
    {
        await WriteAsync(Destination, "changed.txt", "deployed version");
        var appBackup = Path.Combine(Backup, "deployment-1", "Test App");
        await WriteAsync(appBackup, "changed.txt", "original version");
        var record = CreateRollbackRecord("Test App", appBackup);
        record.Applications.Add(CreateRollbackApplication(
            "Another API",
            Path.Combine(_root, "not-deployed"),
            Path.Combine(Backup, "not-deployed"),
            ["pending.txt"],
            ["pending.txt"]));

        await CreateRollbackService().RollbackAsync(record, "Test App");

        Assert.Equal("Partially rolled back", record.Status);
        Assert.Null(record.Applications[1].RolledBackAtUtc);
    }

    [Fact]
    public async Task Deploy_BackupExclusionsDoNotCopyExcludedOriginals()
    {
        await WriteAsync(Source, "app.log", "new log", "logs");
        await WriteAsync(Destination, "app.log", "old log", "logs");
        File.SetLastWriteTimeUtc(Path.Combine(Source, "logs", "app.log"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(Destination, "logs", "app.log"), DateTime.UtcNow.AddMinutes(-2));
        var mapping = CreateMapping();
        mapping.BackupExclusions = ["logs"];
        var plan = await CreatePlanner().CreatePlanAsync(mapping, hashComparisonEnabled: false);

        var results = await CreateDeploymentService().DeployAsync([plan], Backup, FastOptions());

        Assert.Equal(0, plan.BackupFileCount);
        Assert.Equal(string.Empty, results[0].BackupPath);
        Assert.Equal("new log", await File.ReadAllTextAsync(Path.Combine(Destination, "logs", "app.log")));
    }

    [Fact]
    public async Task Deploy_ConfigurationIniAppendsNewEntriesWithoutChangingDestinationValues()
    {
        const string sourceConfiguration = "[Application]\r\nConnectionString=source-value\r\nFeatureEnabled=true\r\n[Logging]\r\nLevel=Debug\r\n";
        const string destinationConfiguration = "[application]\r\nConnectionString=destination-value\r\n";
        await WriteAsync(Source, "configuration.ini", sourceConfiguration);
        await WriteAsync(Destination, "configuration.ini", destinationConfiguration);

        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);
        var results = await CreateDeploymentService().DeployAsync([plan], Backup, FastOptions());
        var deployedContent = await File.ReadAllTextAsync(Path.Combine(Destination, "configuration.ini"));

        Assert.Single(plan.ChangedFiles);
        Assert.Equal(1, plan.BackupFileCount);
        Assert.Contains("ConnectionString=destination-value", deployedContent);
        Assert.DoesNotContain("ConnectionString=source-value", deployedContent);
        Assert.Contains("FeatureEnabled=true", deployedContent);
        Assert.Contains("[Logging]", deployedContent);
        Assert.Contains("Level=Debug", deployedContent);
        Assert.Single(Regex.Matches(deployedContent, @"(?im)^\s*\[Application\]\s*$").Cast<Match>());
        Assert.Contains("configuration.ini", results[0].DeployedFiles);
        Assert.Equal(destinationConfiguration, await File.ReadAllTextAsync(Path.Combine(results[0].BackupPath, "configuration.ini")));
    }

    [Fact]
    public async Task Deploy_ConfigurationIniAddsKeyInsideExistingSectionWithoutRepeatingHeader()
    {
        await WriteAsync(Source, "configuration.ini", "[UserManagementAPISettings]\r\nExistingSetting=source-value\r\nNewSetting=added-value\r\n");
        await WriteAsync(Destination, "configuration.ini", "[UserManagementAPISettings]\r\nExistingSetting=destination-value\r\n[Logging]\r\nLevel=Info\r\n");

        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);
        await CreateDeploymentService().DeployAsync([plan], Backup, FastOptions());
        var deployedContent = await File.ReadAllTextAsync(Path.Combine(Destination, "configuration.ini"));

        Assert.Equal(1, deployedContent.Split("[UserManagementAPISettings]", StringSplitOptions.None).Length - 1);
        Assert.Contains("ExistingSetting=destination-value", deployedContent);
        Assert.DoesNotContain("ExistingSetting=source-value", deployedContent);
        Assert.True(deployedContent.IndexOf("NewSetting=added-value", StringComparison.Ordinal)
            < deployedContent.IndexOf("[Logging]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Planner_ConfigurationIniWithExistingKeysOnlyIsUnchanged()
    {
        await WriteAsync(Source, "configuration.ini", "[App]\nMode=SourceValue\n", "wwwroot");
        await WriteAsync(Destination, "configuration.ini", "[app]\nMode=DestinationValue\n", "wwwroot");

        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: true);

        Assert.Empty(plan.ChangedFiles);
        Assert.Single(plan.UnchangedFiles);
        Assert.Equal(0, plan.BackupFileCount);
    }

    [Fact]
    public async Task Configuration_LastDeploymentDateAndFilesPersistAcrossLoad()
    {
        var settingsPath = Path.Combine(_root, "settings.json");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeploymentManager:SettingsPath"] = settingsPath
            })
            .Build();
        var service = new JsonConfigurationService(configuration, NullLogger<JsonConfigurationService>.Instance);
        var deployedAt = DateTime.UtcNow;
        var expectedRecord = new LastDeploymentRecord
        {
            DeploymentId = "deployment-123",
            DeployedAtUtc = deployedAt,
            Status = "Failed",
            Files =
            [
                new() { ApplicationName = "UserManagementAPI", RelativePath = "bin\\Api.dll" },
                new() { ApplicationName = "UserManagementAPI", RelativePath = "configuration.ini" }
            ],
            Applications =
            [
                new()
                {
                    ApplicationName = "UserManagementAPI",
                    DestinationFolder = Destination,
                    BackupDirectory = Backup,
                    CreatedFiles = ["bin\\Api.dll"],
                    ChangedFiles = ["configuration.ini"],
                    BackedUpFiles = ["configuration.ini"]
                }
            ]
        };

        var mapping = new DeploymentMapping
        {
            Name = "UserManagementAPI",
            ManageIisAppPool = true,
            IisServer = "WEB01",
            IisAppPoolName = "UserManagementAPI"
        };
        await service.SaveAsync(new DeploymentConfiguration
        {
            LastDeployment = expectedRecord,
            Mappings = [mapping]
        });
        var loaded = await service.LoadAsync();

        Assert.Equal(deployedAt, loaded.LastDeployment.DeployedAtUtc);
        Assert.Equal(2, loaded.LastDeployment.Files.Count);
        Assert.Equal("bin\\Api.dll", loaded.LastDeployment.Files[0].RelativePath);
        Assert.Equal("configuration.ini", loaded.LastDeployment.Files[1].RelativePath);
        Assert.Equal("deployment-123", loaded.LastDeployment.DeploymentId);
        Assert.Equal("Failed", loaded.LastDeployment.Status);
        Assert.Equal("configuration.ini", loaded.LastDeployment.Applications[0].BackedUpFiles[0]);
        Assert.True(loaded.Mappings[0].ManageIisAppPool);
        Assert.Equal("WEB01", loaded.Mappings[0].IisServer);
        Assert.Equal("UserManagementAPI", loaded.Mappings[0].IisAppPoolName);
    }

    [Fact]
    public async Task Deploy_RejectsStalePreviewBeforeWritingAnyFiles()
    {
        await WriteAsync(Source, "first.txt", "first");
        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);
        await WriteAsync(Source, "added-after-preview.txt", "second");

        await Assert.ThrowsAsync<DeploymentPlanStaleException>(() =>
            CreateDeploymentService().DeployAsync([plan], Backup, FastOptions()));

        Assert.False(File.Exists(Path.Combine(Destination, "first.txt")));
    }

    [Fact]
    public async Task Deploy_RejectsDestinationFileCreatedAfterPreview()
    {
        await WriteAsync(Source, "new.txt", "source version");
        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);
        await WriteAsync(Destination, "new.txt", "existing version");

        await Assert.ThrowsAsync<DeploymentPlanStaleException>(() =>
            CreateDeploymentService().DeployAsync([plan], Backup, FastOptions()));

        Assert.Equal("existing version", await File.ReadAllTextAsync(Path.Combine(Destination, "new.txt")));
    }

    [Fact]
    public async Task Deploy_CancellationBeforeCopyLeavesDestinationUnchanged()
    {
        await WriteAsync(Source, "file.txt", "replacement");
        await WriteAsync(Destination, "file.txt", "original");
        var plan = await CreatePlanner().CreatePlanAsync(CreateMapping(), hashComparisonEnabled: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateDeploymentService().DeployAsync([plan], Backup, FastOptions(), cancellationToken: cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Destination, "file.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DeploymentMapping CreateMapping() => new()
    {
        Name = "Test App",
        SourceFolder = Source,
        DestinationFolder = Destination,
        BackupEnabled = true
    };

    private DeploymentPlanner CreatePlanner() => new(new IniConfigurationMerger(), NullLogger<DeploymentPlanner>.Instance);

    private DeploymentService CreateDeploymentService(IIisApplicationPoolService? iisService = null) => new(
        CreatePlanner(),
        new IniConfigurationMerger(),
        iisService ?? new FakeIisApplicationPoolService(),
        new JavaScriptScaffoldingService(),
        NullLogger<DeploymentService>.Instance);

    private DeploymentRollbackService CreateRollbackService(IIisApplicationPoolService? iisService = null) => new(
        iisService ?? new FakeIisApplicationPoolService(),
        NullLogger<DeploymentRollbackService>.Instance);

    private LastDeploymentRecord CreateRollbackRecord(string applicationName, string backupDirectory) => new()
    {
        Applications =
        [
            CreateRollbackApplication(applicationName, Destination, backupDirectory, ["changed.txt"], ["changed.txt"], ["new.txt"])
        ]
    };

    private static DeploymentApplicationRecord CreateRollbackApplication(
        string name,
        string destination,
        string backupDirectory,
        List<string> changedFiles,
        List<string> backedUpFiles,
        List<string>? createdFiles = null) => new()
    {
        ApplicationName = name,
        DestinationFolder = destination,
        BackupDirectory = backupDirectory,
        ManageIisAppPool = name == "Test App",
        IisAppPoolName = "TestPool",
        ChangedFiles = changedFiles,
        BackedUpFiles = backedUpFiles,
        CreatedFiles = createdFiles ?? []
    };

    private sealed class FakeIisApplicationPoolService : IIisApplicationPoolService
    {
        public List<string> Calls { get; } = [];
        public bool WasRunning { get; init; } = true;
        public bool IsStopped { get; private set; }
        public bool IsStoppedDuringStart { get; private set; }
        public bool RestartUsedNonCancelledToken { get; private set; }
        public bool FailToStart { get; init; }
        public Action? AfterStop { get; init; }

        public Task<bool> StopIfRunningAsync(DeploymentMapping mapping, CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            IsStopped = WasRunning;
            AfterStop?.Invoke();
            return Task.FromResult(WasRunning);
        }

        public Task StartAsync(DeploymentMapping mapping, CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            IsStoppedDuringStart = IsStopped;
            RestartUsedNonCancelledToken = !cancellationToken.IsCancellationRequested;
            if (FailToStart)
            {
                return Task.FromException(new InvalidOperationException("simulated IIS start failure"));
            }

            IsStopped = false;
            return Task.CompletedTask;
        }
    }

    private static DeploymentOptions FastOptions() => new()
    {
        VerifyFilesAfterCopy = true,
        RetryAttempts = 0,
        RetryDelayMilliseconds = 0
    };

    private static async Task WriteAsync(string root, string name, string content, string? relativeDirectory = null)
    {
        var directory = relativeDirectory is null ? root : Path.Combine(root, relativeDirectory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), content);
    }
}