using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeploymentManager.Models;
using Microsoft.Extensions.Logging;

namespace DeploymentManager.Services;

public sealed class PowerShellIisApplicationPoolService(ILogger<PowerShellIisApplicationPoolService> logger)
    : IIisApplicationPoolService
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(90);

    public async Task<bool> StopIfRunningAsync(DeploymentMapping mapping, CancellationToken cancellationToken = default)
    {
        ValidateMapping(mapping);
        var output = await RunPowerShellAsync(mapping, "Stop", cancellationToken);
        var finalStatusLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        var wasRunning = string.Equals(finalStatusLine, "STOPPED", StringComparison.OrdinalIgnoreCase);
        logger.LogInformation(
            wasRunning
                ? "Stopped IIS application pool {ApplicationPool} on {IisServer}"
                : "IIS application pool {ApplicationPool} was already stopped on {IisServer}",
            mapping.IisAppPoolName,
            string.IsNullOrWhiteSpace(mapping.IisServer) ? "localhost" : mapping.IisServer);
        return wasRunning;
    }

    public async Task StartAsync(DeploymentMapping mapping, CancellationToken cancellationToken = default)
    {
        ValidateMapping(mapping);
        await RunPowerShellAsync(mapping, "Start", cancellationToken);
        logger.LogInformation(
            "Started IIS application pool {ApplicationPool} on {IisServer}",
            mapping.IisAppPoolName,
            string.IsNullOrWhiteSpace(mapping.IisServer) ? "localhost" : mapping.IisServer);
    }

    private async Task<string> RunPowerShellAsync(
        DeploymentMapping mapping,
        string action,
        CancellationToken cancellationToken)
    {
        var poolName = Convert.ToBase64String(Encoding.UTF8.GetBytes(mapping.IisAppPoolName));
        var serverName = Convert.ToBase64String(Encoding.UTF8.GetBytes(mapping.IisServer ?? string.Empty));
        var runLocally = IsLocalServer(mapping.IisServer) ? "$true" : "$false";
        var script = $$"""
            $poolName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{poolName}}'))
            $serverName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{serverName}}'))
            $runLocally = {{runLocally}}
            $action = '{{action}}'
            $poolOperation = {
                param([string]$Name, [string]$Operation)
                $ErrorActionPreference = 'Stop'
                Import-Module WebAdministration -ErrorAction Stop
                if ($Operation -eq 'Stop') {
                    $state = (Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value
                    if ($state -ne 'Started') { return 'ALREADY_STOPPED' }
                    Stop-WebAppPool -Name $Name -ErrorAction Stop
                    $deadline = (Get-Date).AddSeconds(60)
                    while ((Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value -ne 'Stopped') {
                        if ((Get-Date) -ge $deadline) { throw "Timed out stopping IIS application pool '$Name'." }
                        Start-Sleep -Milliseconds 500
                    }
                    return 'STOPPED'
                }
                Start-WebAppPool -Name $Name -ErrorAction Stop
                $deadline = (Get-Date).AddSeconds(60)
                while ((Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value -ne 'Started') {
                    if ((Get-Date) -ge $deadline) { throw "Timed out starting IIS application pool '$Name'." }
                    Start-Sleep -Milliseconds 500
                }
                return 'STARTED'
            }
            if ($runLocally) {
                & $poolOperation $poolName $action
            }
            else {
                Invoke-Command -ComputerName $serverName -ScriptBlock $poolOperation -ArgumentList $poolName, $action -ErrorAction Stop
            }
            """;

        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start Windows PowerShell to control the IIS application pool.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out while trying to {action.ToLowerInvariant()} IIS application pool '{mapping.IisAppPoolName}'.");
            }

            throw;
        }

        var standardOutput = await outputTask;
        var standardError = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException(
                $"Could not {action.ToLowerInvariant()} IIS application pool '{mapping.IisAppPoolName}' on '{mapping.IisServer}'. {details.Trim()}");
        }

        return standardOutput;
    }

    public static bool IsLocalServer(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return true;
        }

        var normalizedName = serverName.Trim().TrimStart('\\');
        if (normalizedName is "." or "localhost" or "127.0.0.1" or "::1"
            || normalizedName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var localAddresses = Dns.GetHostAddresses(Dns.GetHostName());
            var targetAddresses = Dns.GetHostAddresses(normalizedName);
            return targetAddresses.Any(target => localAddresses.Contains(target));
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void ValidateMapping(DeploymentMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.IisAppPoolName))
        {
            throw new InvalidOperationException($"An IIS application pool name is required for '{mapping.Name}'.");
        }
    }
}