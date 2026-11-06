param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is missing. Install .NET Framework 4.8 or use a Windows build machine.' }
$output = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$exePath = Join-Path $output 'SheetSetRelinker.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.Core.dll /r:System.Web.Extensions.dll "/out:$exePath" $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Write-Host "Built $exePath"
if ($Test) {
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object FullName)
    $testExe = Join-Path $output 'RelinkerTests.exe'
    & $compiler /nologo /target:exe /main:TestMain /platform:anycpu /utf8output /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.Core.dll /r:System.Web.Extensions.dll "/r:$exePath" "/out:$testExe" $testFiles
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe (Join-Path $projectRoot ('test-output\' + [Guid]::NewGuid().ToString('N').Substring(0,8)))
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

