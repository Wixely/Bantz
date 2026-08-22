[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipConsumerSmoke
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageRoot = Join-Path $repoRoot 'artifacts/packages'
$consumerRoot = Join-Path $repoRoot 'artifacts/package-consumer'
$projects = @(
    'src/Bantz.Speech.Abstractions/Bantz.Speech.Abstractions.csproj',
    'src/Bantz.Speech.Whisper/Bantz.Speech.Whisper.csproj',
    'src/Bantz.Capture/Bantz.Capture.csproj',
    'src/Bantz.Input/Bantz.Input.csproj'
)

foreach ($path in @($packageRoot, $consumerRoot)) {
    $resolvedParent = [IO.Path]::GetFullPath((Split-Path $path -Parent))
    if (-not $resolvedParent.StartsWith([IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the artifacts directory: $path"
    }
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $packageRoot, $consumerRoot | Out-Null

Push-Location $repoRoot
try {
    foreach ($project in $projects) {
        dotnet pack $project -c $Configuration -o $packageRoot
        if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
    }

    $packages = @(Get-ChildItem -LiteralPath $packageRoot -Filter 'Bantz.*.nupkg' -File)
    $symbols = @(Get-ChildItem -LiteralPath $packageRoot -Filter 'Bantz.*.snupkg' -File)
    if ($packages.Count -ne 4 -or $symbols.Count -ne 4) {
        throw "Expected four packages and four symbol packages; found $($packages.Count) and $($symbols.Count)."
    }

    if (-not $SkipConsumerSmoke) {
        $sample = 'samples/MinimalDictation/MinimalDictation.csproj'
        dotnet restore $sample --configfile 'samples/MinimalDictation/NuGet.config' `
            -p:RestorePackagesPath=$consumerRoot
        if ($LASTEXITCODE -ne 0) { throw 'Package-consumer restore failed.' }
        dotnet build $sample -c $Configuration --no-restore -p:RestorePackagesPath=$consumerRoot
        if ($LASTEXITCODE -ne 0) { throw 'Package-consumer build failed.' }
        dotnet run --project $sample -c $Configuration --no-build -p:RestorePackagesPath=$consumerRoot
        if ($LASTEXITCODE -ne 0) { throw 'Package-consumer execution failed.' }
    }

    Write-Output "Created and verified four Bantz packages in $packageRoot"
}
finally {
    Pop-Location
}
