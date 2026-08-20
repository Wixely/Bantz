[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('windows-x64', 'linux-x64')]
    [string] $Platform,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = (dotnet msbuild (Join-Path $repoRoot 'src/Bantz.App/Bantz.App.csproj') -getProperty:Version).Trim()
if (-not $version) { throw 'Could not read the Bantz version.' }

$isWindowsPackage = $Platform -eq 'windows-x64'
$project = if ($isWindowsPackage) { 'src/Bantz.Windows/Bantz.Windows.csproj' } else { 'src/Bantz.Linux/Bantz.Linux.csproj' }
$rid = if ($isWindowsPackage) { 'win-x64' } else { 'linux-x64' }
$binary = if ($isWindowsPackage) { 'Bantz.Windows.exe' } else { 'Bantz.Linux' }
$assetBase = "bantz-v$version-$rid"
$standaloneName = if ($isWindowsPackage) { "$assetBase.exe" } else { $assetBase }
$releaseRoot = Join-Path $repoRoot 'artifacts/release'
$publishRoot = Join-Path $releaseRoot "publish-$rid"
$stageRoot = Join-Path $releaseRoot $assetBase

foreach ($path in @($publishRoot, $stageRoot)) {
    if ((Test-Path $path) -and $path.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
New-Item -ItemType Directory -Force $publishRoot, $stageRoot | Out-Null

function Write-Checksum([string] $Path)
{
    $checksumPath = "$Path.sha256"
    $checksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    $checksumLine = "$checksum  $([IO.Path]::GetFileName($Path))`n"
    [IO.File]::WriteAllText($checksumPath, $checksumLine, (New-Object Text.UTF8Encoding($false)))
    return $checksumPath
}

Push-Location $repoRoot
try {
    dotnet publish $project -c $Configuration -r $rid --self-contained true -o $publishRoot `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "$Platform publish failed." }

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishRoot -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne $binary) {
        throw "$Platform publish did not produce exactly one standalone executable."
    }

    Copy-Item -LiteralPath (Join-Path $publishRoot $binary) -Destination $stageRoot
    $standalone = Join-Path $releaseRoot $standaloneName
    Copy-Item -LiteralPath (Join-Path $publishRoot $binary) -Destination $standalone -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stageRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stageRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $stageRoot

    if (-not $isWindowsPackage) {
        & chmod +x (Join-Path $stageRoot $binary)
        if ($LASTEXITCODE -ne 0) { throw 'Could not mark the Linux executable as executable.' }
        & chmod +x $standalone
        if ($LASTEXITCODE -ne 0) { throw 'Could not mark the standalone Linux executable as executable.' }
    }

    $snapshot = Join-Path $releaseRoot "$assetBase-smoke.png"
    $smoke = Start-Process -FilePath (Join-Path $stageRoot $binary) `
        -ArgumentList @('--page', 'main', '--snapshot', "`"$snapshot`"") `
        -Wait -PassThru
    if ($smoke.ExitCode -ne 0 -or -not (Test-Path $snapshot)) { throw "$Platform smoke test failed." }

    $archive = if ($isWindowsPackage) {
        $path = Join-Path $releaseRoot "$assetBase.zip"
        Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $path -CompressionLevel Optimal -Force
        $path
    }
    else {
        $path = Join-Path $releaseRoot "$assetBase.tar.gz"
        & tar -czf $path -C $stageRoot .
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the Linux release archive.' }
        $path
    }

    $standaloneChecksumPath = Write-Checksum $standalone
    $checksumPath = Write-Checksum $archive
    Write-Output "Created $standalone"
    Write-Output "Created $standaloneChecksumPath"
    Write-Output "Created $archive"
    Write-Output "Created $checksumPath"
}
finally {
    Pop-Location
}
