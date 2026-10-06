using System.Text.Json;
using System.IO;
using DeploymentManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.Services;

public sealed class JsonConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogger<JsonConfigurationService> _logger;

    public JsonConfigurationService(IConfiguration configuration, ILogger<JsonConfigurationService> logger)
    {
        _logger = logger;
        SettingsPath = configuration["DeploymentManager:SettingsPath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeploymentManager", "settings.json");
    }

    public string SettingsPath { get; }

    public async Task<DeploymentConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = DeploymentConfiguration.CreateDefault();
            await SaveAsync(defaults, cancellationToken);
            return defaults;
        }

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync<DeploymentConfiguration>(stream, SerializerOptions, cancellationToken)
                ?? DeploymentConfiguration.CreateDefault();
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Configuration file {SettingsPath} contains invalid JSON", SettingsPath);
            throw new InvalidDataException($"The configuration file is invalid: {SettingsPath}", exception);
        }
    }

    public async Task SaveAsync(DeploymentConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The settings path does not have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
            _logger.LogInformation("Saved deployment configuration to {SettingsPath}", SettingsPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}