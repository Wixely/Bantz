[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        dotnet restore Bantz.slnx -p:NuGetAudit=true -p:NuGetAuditMode=all
        if ($LASTEXITCODE -ne 0) { throw 'Restore or NuGet audit failed.' }
    }

    $ownedProjects = @(
        'src/Bantz.Speech.Abstractions/Bantz.Speech.Abstractions.csproj',
        'src/Bantz.Speech.Whisper/Bantz.Speech.Whisper.csproj',
        'src/Bantz.Capture/Bantz.Capture.csproj',
        'src/Bantz.Input/Bantz.Input.csproj',
        'src/Bantz.Core/Bantz.Core.csproj',
        'src/Bantz.App/Bantz.App.csproj',
        'src/Bantz.Windows/Bantz.Windows.csproj',
        'src/Bantz.Linux/Bantz.Linux.csproj',
        'tests/Bantz.Core.Tests/Bantz.Core.Tests.csproj',
        'tests/Bantz.Packages.Tests/Bantz.Packages.Tests.csproj'
    )

    foreach ($project in $ownedProjects) {
        dotnet format $project --verify-no-changes --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Formatting verification failed for $project." }
    }

    dotnet build Bantz.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $results = Join-Path $repoRoot 'artifacts/test-results'
    New-Item -ItemType Directory -Force $results | Out-Null
    $testProjects = @(
        'tests/Bantz.Core.Tests/Bantz.Core.Tests.csproj',
        'tests/Bantz.Packages.Tests/Bantz.Packages.Tests.csproj'
    )
    foreach ($project in $testProjects) {
        $resultName = "$([IO.Path]::GetFileNameWithoutExtension($project)).trx"
        dotnet test $project -c $Configuration --no-build --logger "trx;LogFileName=$resultName" --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw "Tests failed for $project." }
    }
}
finally {
    Pop-Location
}
