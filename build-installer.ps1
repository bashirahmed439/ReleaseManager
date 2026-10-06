param(
    [ValidatePattern('^\d+(\.\d+){0,3}$')]
    [string]$Version = "1.0.0",
    [string]$CompilerPath
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$projectPath = Join-Path $root "DeploymentManager\DeploymentManager.csproj"
$installerPath = Join-Path $root "installer\DeploymentManager.iss"
$publishPath = Join-Path $root "artifacts\publish\win-x64"
$outputPath = Join-Path $root "artifacts\installer"

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    $CompilerPath = $compilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path $CompilerPath)) {
    throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup or pass -CompilerPath."
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishPath `
    "/p:Version=$Version"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

& $CompilerPath "/DAppVersion=$Version" $installerPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installerFile = Join-Path $outputPath "DeploymentManager-Setup-$Version-x64.exe"
Write-Host "Installer created: $installerFile"