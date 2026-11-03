[CmdletBinding()]
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project tests/Pitstop.Tests/Pitstop.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Session tests failed.' }
    $publish = Join-Path $PSScriptRoot "work/publish/$Runtime"
    dotnet publish src/Pitstop.Windows/Pitstop.Windows.csproj -c Release -r $Runtime --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item README.md, VALIDATION.md, LICENSE, NOTICE.md -Destination $publish
    $dist = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $archive = Join-Path $dist "Pitstop-$Runtime.zip"
    Compress-Archive -Path "$publish/*" -DestinationPath $archive -Force
    Write-Host "Built $archive"
} finally { Pop-Location }
