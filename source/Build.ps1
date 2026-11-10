param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$target = Join-Path $OutputDirectory 'MP4Lite.exe'
$compileArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', "/out:$target", "/win32manifest:$PSScriptRoot\app.manifest", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll')
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'app.ico')) { $compileArgs += "/win32icon:$PSScriptRoot\app.ico" }
& $compiler @compileArgs (Join-Path $PSScriptRoot 'App.cs') (Join-Path $PSScriptRoot 'Engine.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MP4Lite.exe.config') -Destination $OutputDirectory -Force
Write-Output "Built: $target"
