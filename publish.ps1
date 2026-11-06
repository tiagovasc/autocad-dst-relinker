param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$DotNet = 'dotnet',
    [string]$BuildRoot = (Join-Path ([IO.Path]::GetTempPath()) 'SheetSetRelinker-build'),
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sdkVersion = & $DotNet --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw 'Publishing requires the .NET 10 SDK. A portable SDK path can be supplied with -DotNet.' }
$buildFolder = [IO.Path]::GetFullPath($BuildRoot)
$publishFolder = Join-Path $projectRoot "dist\$Runtime"
New-Item -ItemType Directory -Path $buildFolder, $publishFolder -Force | Out-Null
$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldFirstRun = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$oldNugetPackages = $env:NUGET_PACKAGES
$oldCliHome = $env:DOTNET_CLI_HOME
$oldCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$oldToolsPath = $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $buildFolder 'packages'
$env:DOTNET_CLI_HOME = Join-Path $buildFolder 'cli-home'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
try {
    & $DotNet publish (Join-Path $projectRoot 'SheetSetRelinker.csproj') --configuration Release --runtime $Runtime --self-contained true --artifacts-path $buildFolder --output $publishFolder -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained application publishing failed.' }
    $exePath = Join-Path $publishFolder 'SheetSetRelinker.exe'
    if (!(Test-Path -LiteralPath $exePath)) { throw 'The expected executable was not produced.' }
    $unexpected = @(Get-ChildItem -LiteralPath $publishFolder -File | Where-Object { $_.Name -notin @('SheetSetRelinker.exe', 'SheetSetRelinker-windows-x64.zip', 'SheetSetRelinker-windows-arm64.zip', 'SHA256SUMS.txt', 'build-info.json') })
    if ($unexpected.Count -ne 0) { throw ('Unexpected sidecar files; check the publish output: ' + ($unexpected.Name -join ', ')) }
    $hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  SheetSetRelinker.exe" | Set-Content -LiteralPath (Join-Path $publishFolder 'SHA256SUMS.txt') -Encoding ascii
    [ordered]@{ application = 'Sheet Set Relinker'; sdk = $sdkVersion; runtime = $Runtime; self_contained = $true; single_file = $true; sha256 = $hash; bytes = (Get-Item -LiteralPath $exePath).Length } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishFolder 'build-info.json') -Encoding utf8
    Write-Host "Published $exePath"
    Write-Host 'The end user needs only this EXE; no .NET or AutoCAD installation is needed to run the tool.'
    if ($Test) {
        if ($Runtime -ne 'win-x64' -and $env:PROCESSOR_ARCHITECTURE -ne 'ARM64') { throw 'Cross-architecture tests must be run on a matching Windows machine.' }
        $testFolder = Join-Path $buildFolder "test-host\$Runtime"
        & $DotNet publish (Join-Path $projectRoot 'tests\RelinkerTests.csproj') --configuration Release --runtime $Runtime --self-contained true --artifacts-path $buildFolder --output $testFolder -p:PublishSingleFile=false
        if ($LASTEXITCODE -ne 0) { throw 'Test application publishing failed.' }
        $evidenceFolder = Join-Path $buildFolder ('test-evidence\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
        & (Join-Path $testFolder 'RelinkerTests.exe') $evidenceFolder
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Review the evidence folder printed above.' }
    }
    $zipName = 'SheetSetRelinker-windows-' + $Runtime.Substring(4) + '.zip'
    $zipPath = Join-Path $publishFolder $zipName
    $zipFiles = @($exePath, (Join-Path $publishFolder 'SHA256SUMS.txt'), (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md'), (Join-Path $projectRoot 'docs\START_HERE.txt'), (Join-Path $projectRoot 'licenses'))
    Compress-Archive -LiteralPath $zipFiles -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $zipName" | Add-Content -LiteralPath (Join-Path $publishFolder 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Packaged $zipPath"
} finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldTelemetry
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $oldFirstRun
    $env:NUGET_PACKAGES = $oldNugetPackages
    $env:DOTNET_CLI_HOME = $oldCliHome
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $oldCertificate
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $oldToolsPath
}
