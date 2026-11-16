param([string]$Python = '', [string]$Output = '', [string]$ApplicationDirectory = '', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
if (-not $Python) { $Python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe' }
if (-not $Output) { $Output = Join-Path $PSScriptRoot 'release\KeepClear-0.3.1-win-x64' }
if (-not $ApplicationDirectory) { $ApplicationDirectory = Join-Path $PSScriptRoot 'dist' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $ApplicationDirectory }
& $Python (Join-Path $PSScriptRoot 'tools\package_portable.py') --output $Output --application-dir $ApplicationDirectory
if ($LASTEXITCODE -ne 0) { throw 'Portable package assembly failed.' }
Write-Host "Assembled: $Output"
& $Python (Join-Path $PSScriptRoot 'tools\validate_portable.py') $Output
if ($LASTEXITCODE -ne 0) { throw 'Portable isolation/model verification failed; no release ZIP was created.' }
& $Python (Join-Path $PSScriptRoot 'tools\package_portable.py') --output $Output --archive-only
if ($LASTEXITCODE -ne 0) { throw 'Portable release archive verification failed.' }
