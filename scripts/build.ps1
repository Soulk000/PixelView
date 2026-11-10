[CmdletBinding()]
param(
    [switch]$Installer,
    [string]$MakeNsisPath,
    [string]$RuntimeVersion = '10.0.14'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repoRoot 'src/PixelView/PixelView.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$project.Project.PropertyGroup.Version
$parsedVersion = [version]$version
$fileVersion = '{0}.{1}.{2}.0' -f $parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build
$packageVersion = if ($parsedVersion.Build -eq 0) { '{0}.{1}' -f $parsedVersion.Major, $parsedVersion.Minor } else { $version }
$artifactPath = Join-Path $repoRoot 'artifacts'
$publishPath = Join-Path $artifactPath ('publish-' + [guid]::NewGuid().ToString('N'))
$stagePath = Join-Path $artifactPath ('portable-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishPath, $stagePath -Force | Out-Null

if ($Installer) {
    if (-not $MakeNsisPath) {
        $nsisCommand = Get-Command makensis.exe -ErrorAction SilentlyContinue
        if ($nsisCommand) { $MakeNsisPath = $nsisCommand.Source }
    }
    if (-not $MakeNsisPath -or -not (Test-Path -LiteralPath $MakeNsisPath -PathType Leaf)) {
        throw 'Install NSIS 3 and provide -MakeNsisPath, or add makensis.exe to PATH.'
    }
    $MakeNsisPath = (Resolve-Path -LiteralPath $MakeNsisPath).Path
}

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $publishPath 'PixelView.exe') -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/使用说明-免安装版.txt') -Destination (Join-Path $stagePath '使用说明.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination (Join-Path $stagePath 'Licenses') -Recurse
$zipPath = Join-Path $artifactPath "PixelView-$packageVersion-Windows-x64.zip"
Compress-Archive -Path (Join-Path $stagePath '*') -DestinationPath $zipPath -Force
Write-Host "Portable package: $zipPath"

if ($Installer) {
    Push-Location (Join-Path $repoRoot 'installer')
    try {
        & $MakeNsisPath /V2 "/DAPP_VERSION=$version" "/DFILE_VERSION=$fileVersion" `
            "/DPACKAGE_VERSION=$packageVersion" "/DPUBLISH_DIR=$publishPath" PixelView.nsi
        if ($LASTEXITCODE -ne 0) { throw 'NSIS build failed.' }
    }
    finally { Pop-Location }
    Write-Host "Installer: $(Join-Path $artifactPath "PixelView-$packageVersion-Setup.exe")"
}

$packages = @($zipPath)
if ($Installer) { $packages += Join-Path $artifactPath "PixelView-$packageVersion-Setup.exe" }
$hashLines = foreach ($package in $packages) {
    $hash = Get-FileHash -LiteralPath $package -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($package)
}
$hashLines | Set-Content -LiteralPath (Join-Path $artifactPath 'SHA256SUMS.txt') -Encoding ascii
