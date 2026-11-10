param([string]$TestDirectory = (Join-Path $env:TEMP ('MP4Lite-tests-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $TestDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$engineDirectory = Join-Path (Split-Path $PSScriptRoot) 'engine'
foreach ($testName in @('IntegrationTests', 'ExtraTests')) {
    $exe = Join-Path $TestDirectory ($testName + '.exe')
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$exe" /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'Engine.cs') (Join-Path $PSScriptRoot ($testName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "$testName compilation failed." }
    & $exe $engineDirectory $TestDirectory
    if ($LASTEXITCODE -ne 0) { throw "$testName failed." }
}
Write-Output "All tests passed. Fixtures and results: $TestDirectory"
