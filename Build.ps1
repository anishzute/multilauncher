[CmdletBinding()]
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path work -Force | Out-Null
    if (-not $SkipTests) {
        dotnet run --project tests/Pitstop.Tests/Pitstop.Tests.csproj -c Release 2>&1 | Tee-Object -FilePath work/test-results.log
        if ($LASTEXITCODE -ne 0) { throw 'Session tests failed.' }
    }
    $publish = Join-Path $PSScriptRoot "work/publish/$Runtime"
    dotnet publish src/Pitstop.Windows/Pitstop.Windows.csproj -c Release -r $Runtime --self-contained true -o $publish 2>&1 | Tee-Object -FilePath work/publish.log
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item README.md, VALIDATION.md, LICENSE, NOTICE.md -Destination $publish
    Get-ChildItem $publish -Filter '*.pdb' | Remove-Item
    $notices = Join-Path $publish 'licenses'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    $assets = Get-Content src/Pitstop.Windows/obj/project.assets.json -Raw | ConvertFrom-Json
    $packageRoots = $assets.packageFolders.PSObject.Properties.Name
    $config = Get-Content "src/Pitstop.Windows/bin/Release/net10.0-windows/$Runtime/Pitstop.runtimeconfig.json" -Raw | ConvertFrom-Json
    $coreVersion = ($config.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $desktopVersion = ($config.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
    function Copy-DependencyNotice([string]$Relative, [string]$Name) {
        foreach ($packageRoot in $packageRoots) {
            $file = Join-Path $packageRoot $Relative
            if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $notices $Name); return }
        }
        throw "Dependency notice not found: $Relative"
    }
    Copy-DependencyNotice "microsoft.netcore.app.runtime.$Runtime/$coreVersion/LICENSE.TXT" 'dotnet-runtime-LICENSE.txt'
    Copy-DependencyNotice "microsoft.netcore.app.runtime.$Runtime/$coreVersion/THIRD-PARTY-NOTICES.TXT" 'dotnet-runtime-THIRD-PARTY-NOTICES.txt'
    Copy-DependencyNotice "microsoft.windowsdesktop.app.runtime.$Runtime/$desktopVersion/LICENSE" 'windows-desktop-LICENSE.txt'
    $management = ($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'System.Management/*' } | Select-Object -First 1).ToLowerInvariant()
    Copy-DependencyNotice "$management/THIRD-PARTY-NOTICES.TXT" 'System.Management-THIRD-PARTY-NOTICES.txt'
    $report = "Pitstop build: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')`nRuntime: $Runtime`n"
    $report += "Executable SHA-256: $((Get-FileHash (Join-Path $publish 'Pitstop.exe') -Algorithm SHA256).Hash.ToLowerInvariant())`n"
    foreach ($log in @('work/test-results.log', 'work/windows-smoke.log', 'work/publish.log')) {
        if (Test-Path $log) { $report += "`n$log`n$(Get-Content $log -Raw)`n" }
    }
    Set-Content -Path (Join-Path $publish 'build-report.txt') -Value $report -Encoding utf8
    $dist = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $archive = Join-Path $dist "Pitstop-Windows-$($Runtime -replace '^win-', '').zip"
    Compress-Archive -Path "$publish/*" -DestinationPath $archive -Force
    Write-Host "Built $archive"
} finally { Pop-Location }
